//go:build windows

package googlecookies

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

func TestSelectGoogleCookies(t *testing.T) {
	rows := []browserCookie{
		{Domain: "messages.google.com", Name: "OSID", Value: "optional"},
		{Domain: ".google.com", Name: "SID", Value: "sid"},
		{Domain: ".google.com", Name: "HSID", Value: "hsid"},
		{Domain: ".google.com", Name: "SSID", Value: "ssid"},
		{Domain: ".google.com", Name: "APISID", Value: "apisid"},
		{Domain: ".google.com", Name: "SAPISID", Value: "sapisid"},
		{Domain: "example.com", Name: "private", Value: "never copy"},
	}
	cookies, err := selectGoogleCookies(rows)
	if err != nil {
		t.Fatal(err)
	}
	if cookies["SID"] != "sid" || cookies["OSID"] != "optional" || cookies["private"] != "" {
		t.Fatalf("unexpected selected cookie names: %v", mapKeys(cookies))
	}
	if _, err := selectGoogleCookies(rows[:3]); err == nil {
		t.Fatal("missing required Google cookies should reject refresh")
	}
}

func TestChromiumCookieQueryUsesPairedProfile(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		conn, err := websocket.Accept(w, r, nil)
		if err != nil {
			t.Error(err)
			return
		}
		defer conn.Close(websocket.StatusNormalClosure, "")
		var request struct {
			ID     int    `json:"id"`
			Method string `json:"method"`
		}
		if err := wsjson.Read(r.Context(), conn, &request); err != nil {
			t.Error(err)
			return
		}
		if request.ID != 1 || request.Method != "Storage.getCookies" {
			t.Errorf("unexpected CDP request: %+v", request)
		}
		_ = wsjson.Write(r.Context(), conn, map[string]any{"id": 1, "result": map[string]any{"cookies": []browserCookie{
			{Domain: ".google.com", Name: "SID", Value: "sid"},
			{Domain: ".google.com", Name: "HSID", Value: "hsid"},
			{Domain: ".google.com", Name: "SSID", Value: "ssid"},
			{Domain: ".google.com", Name: "APISID", Value: "apisid"},
			{Domain: ".google.com", Name: "SAPISID", Value: "sapisid"},
		}}})
	}))
	defer server.Close()
	profile := t.TempDir()
	port := strings.TrimPrefix(server.URL, "http://127.0.0.1:")
	if err := os.WriteFile(filepath.Join(profile, "DevToolsActivePort"), []byte(fmt.Sprintf("%s\n/devtools/browser/test\n", port)), 0o600); err != nil {
		t.Fatal(err)
	}
	cookies, err := loadChromiumCookies(context.Background(), profile, "edge")
	if err != nil {
		t.Fatal(err)
	}
	if cookies["SAPISID"] != "sapisid" {
		t.Fatalf("cookie query returned unexpected names: %v", mapKeys(cookies))
	}
}

func mapKeys(m map[string]string) []string {
	keys := make([]string, 0, len(m))
	for key := range m {
		keys = append(keys, key)
	}
	return keys
}
