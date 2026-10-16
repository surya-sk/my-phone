//go:build windows

package googlecookies

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

// The Windows client records the browser used for pairing. Its dedicated,
// persistent profile is the source of fresh cookies after the app was closed.
func NativeSupported() bool {
	browser := strings.ToLower(os.Getenv("OPENMESSAGE_AUTH_BROWSER"))
	profile := DefaultChromeProfile()
	if profile == "" {
		return false
	}
	switch browser {
	case "firefox":
		_, err := os.Stat(filepath.Join(profile, "cookies.sqlite"))
		return err == nil
	case "edge", "chrome":
		return chromiumExecutable(browser) != ""
	default:
		return false
	}
}

func defaultChromeProfileDir(home string) string {
	base := os.Getenv("LOCALAPPDATA")
	if base == "" {
		base = filepath.Join(home, "AppData", "Local")
	}
	return filepath.Join(base, "NativeMessages", "BrowserAuth", strings.ToLower(os.Getenv("OPENMESSAGE_AUTH_BROWSER")))
}

func loadBrowserCookies(ctx context.Context, profile string) (map[string]string, error) {
	switch strings.ToLower(os.Getenv("OPENMESSAGE_AUTH_BROWSER")) {
	case "firefox":
		return loadFirefoxCookies(profile)
	case "edge", "chrome":
		return loadChromiumCookies(ctx, profile, strings.ToLower(os.Getenv("OPENMESSAGE_AUTH_BROWSER")))
	default:
		return nil, fmt.Errorf("no paired browser selected for automatic cookie refresh")
	}
}

func chromiumExecutable(browser string) string {
	var candidates []string
	for _, base := range []string{os.Getenv("PROGRAMFILES"), os.Getenv("PROGRAMFILES(X86)"), os.Getenv("LOCALAPPDATA")} {
		if base == "" {
			continue
		}
		if browser == "edge" {
			candidates = append(candidates, filepath.Join(base, "Microsoft", "Edge", "Application", "msedge.exe"))
		} else {
			candidates = append(candidates, filepath.Join(base, "Google", "Chrome", "Application", "chrome.exe"))
		}
	}
	for _, candidate := range candidates {
		if _, err := os.Stat(candidate); err == nil {
			return candidate
		}
	}
	return ""
}

func loadChromiumCookies(ctx context.Context, profile, browser string) (map[string]string, error) {
	portFile := filepath.Join(profile, "DevToolsActivePort")
	endpoint, err := devToolsEndpoint(portFile)
	if err == nil {
		// A crashed browser can leave its port file behind. Probe before deciding
		// that the profile is still open, otherwise the refresh never restarts it.
		address := strings.TrimPrefix(strings.SplitN(endpoint, "/devtools/", 2)[0], "ws://")
		probe, probeErr := net.DialTimeout("tcp", address, 300*time.Millisecond)
		if probeErr == nil {
			_ = probe.Close()
		} else {
			endpoint = ""
			err = probeErr
			_ = os.Remove(portFile)
		}
	}
	var launched *exec.Cmd
	if err != nil {
		executable := chromiumExecutable(browser)
		if executable == "" {
			return nil, fmt.Errorf("%s is not installed", browser)
		}
		launched = exec.CommandContext(ctx, executable,
			"--headless=new", "--no-first-run", "--remote-debugging-port=0",
			"--user-data-dir="+profile, "https://accounts.google.com/")
		if err := launched.Start(); err != nil {
			return nil, fmt.Errorf("start %s cookie refresh: %w", browser, err)
		}
		defer func() { _ = launched.Process.Kill(); _ = launched.Wait() }()
		for attempt := 0; attempt < 80; attempt++ {
			endpoint, err = devToolsEndpoint(portFile)
			if err == nil {
				break
			}
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(100 * time.Millisecond):
			}
		}
		if err != nil {
			return nil, fmt.Errorf("%s browser profile is unavailable; close its sign-in window and retry: %w", browser, err)
		}
		// Let the signed-in account page rotate cookies before taking the snapshot.
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}

	conn, _, err := websocket.Dial(ctx, endpoint, nil)
	if err != nil {
		return nil, fmt.Errorf("connect to %s cookie service: %w", browser, err)
	}
	defer conn.Close(websocket.StatusNormalClosure, "")
	if err := wsjson.Write(ctx, conn, map[string]any{"id": 1, "method": "Storage.getCookies"}); err != nil {
		return nil, err
	}
	for {
		var response struct {
			ID     int             `json:"id"`
			Result json.RawMessage `json:"result"`
			Error  *struct {
				Message string `json:"message"`
			} `json:"error"`
		}
		if err := wsjson.Read(ctx, conn, &response); err != nil {
			return nil, err
		}
		if response.ID != 1 {
			continue
		}
		if response.Error != nil {
			return nil, fmt.Errorf("browser cookie query: %s", response.Error.Message)
		}
		var result struct {
			Cookies []browserCookie `json:"cookies"`
		}
		if err := json.Unmarshal(response.Result, &result); err != nil {
			return nil, err
		}
		return selectGoogleCookies(result.Cookies)
	}
}

func devToolsEndpoint(portFile string) (string, error) {
	raw, err := os.ReadFile(portFile)
	if err != nil {
		return "", err
	}
	parts := strings.Split(strings.TrimSpace(string(raw)), "\n")
	if len(parts) < 2 || !strings.HasPrefix(parts[1], "/devtools/browser/") {
		return "", fmt.Errorf("invalid browser debugging endpoint")
	}
	port, err := strconv.Atoi(strings.TrimSpace(parts[0]))
	if err != nil || port < 1 || port > 65535 {
		return "", fmt.Errorf("invalid browser debugging port")
	}
	return fmt.Sprintf("ws://127.0.0.1:%d%s", port, strings.TrimSpace(parts[1])), nil
}

func loadFirefoxCookies(profile string) (map[string]string, error) {
	src := filepath.Join(profile, "cookies.sqlite")
	tmp, err := os.MkdirTemp("", "om-firefox-cookies-")
	if err != nil {
		return nil, err
	}
	defer os.RemoveAll(tmp)
	dst := filepath.Join(tmp, "cookies.sqlite")
	if err := copyFile(src, dst); err != nil {
		return nil, err
	}
	for _, suffix := range []string{"-wal", "-shm"} {
		if _, err := os.Stat(src + suffix); err == nil {
			if err := copyFile(src+suffix, dst+suffix); err != nil {
				return nil, err
			}
		}
	}
	db, err := sql.Open("sqlite", dst)
	if err != nil {
		return nil, err
	}
	defer db.Close()
	rows, err := db.Query(`SELECT host, name, value FROM moz_cookies WHERE host IN ('.google.com', 'google.com', 'messages.google.com', 'accounts.google.com')`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var cookies []browserCookie
	for rows.Next() {
		var cookie browserCookie
		if err := rows.Scan(&cookie.Domain, &cookie.Name, &cookie.Value); err != nil {
			return nil, err
		}
		cookies = append(cookies, cookie)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	return selectGoogleCookies(cookies)
}

type browserCookie struct {
	Domain string `json:"domain"`
	Name   string `json:"name"`
	Value  string `json:"value"`
}

func selectGoogleCookies(cookies []browserCookie) (map[string]string, error) {
	selected := make(map[string]string)
	for _, cookie := range cookies {
		if cookie.Value == "" {
			continue
		}
		if cookie.Domain == ".google.com" || cookie.Domain == "google.com" {
			selected[cookie.Name] = cookie.Value
		} else if cookie.Domain == "messages.google.com" && cookie.Name == "OSID" {
			selected[cookie.Name] = cookie.Value
		}
	}
	var missing []string
	for _, required := range requiredCookies {
		if selected[required.name] == "" {
			missing = append(missing, required.name)
		}
	}
	if len(missing) > 0 {
		return nil, fmt.Errorf("selected browser is not signed in to Google (missing %s)", strings.Join(missing, ", "))
	}
	return selected, nil
}
