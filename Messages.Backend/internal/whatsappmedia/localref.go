package whatsappmedia

import (
	"encoding/base64"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

const localMediaPrefix = "walocal:"

func DefaultDesktopRoot() string {
	home, _ := os.UserHomeDir()
	return filepath.Join(home, "Library", "Group Containers", "group.net.whatsapp.WhatsApp.shared", "Message")
}

func EncodeLocalMediaRef(relativePath string) string {
	normalized := filepath.ToSlash(strings.TrimSpace(relativePath))
	if normalized == "" {
		return ""
	}
	return localMediaPrefix + base64.RawURLEncoding.EncodeToString([]byte(normalized))
}

func DecodeLocalMediaRef(value string) (string, error) {
	if !strings.HasPrefix(value, localMediaPrefix) {
		return "", fmt.Errorf("invalid WhatsApp local media reference")
	}
	raw, err := base64.RawURLEncoding.DecodeString(strings.TrimPrefix(value, localMediaPrefix))
	if err != nil {
		return "", fmt.Errorf("decode WhatsApp local media reference: %w", err)
	}
	return string(raw), nil
}

func ResolveLocalMediaPath(root, value string) (string, error) {
	relativePath, err := DecodeLocalMediaRef(value)
	if err != nil {
		return "", err
	}
	if strings.TrimSpace(root) == "" {
		root = DefaultDesktopRoot()
	}
	root = filepath.Clean(root)
	candidate := filepath.Clean(filepath.Join(root, filepath.FromSlash(relativePath)))
	if candidate != root && !strings.HasPrefix(candidate, root+string(filepath.Separator)) {
		return "", fmt.Errorf("whatsapp local media path escapes root")
	}
	return candidate, nil
}

func IsLocalMediaRef(value string) bool {
	return strings.HasPrefix(value, localMediaPrefix)
}
