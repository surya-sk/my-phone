package importer

import (
	"strings"
	"testing"
)

func TestWhatsAppImport(t *testing.T) {
	store := testStore(t)

	chatText := `[2/10/25, 3:45:22 PM] Alice: Hey, how are you?
[2/10/25, 3:46:00 PM] Bob: I'm good!
[2/10/25, 3:47:00 PM] Alice: Want to grab coffee?
[2/10/25, 3:47:30 PM] Bob: Sure, let me check my schedule
and get back to you
[2/10/25, 3:48:00 PM] Alice: <Media omitted>
`

	importer := &WhatsApp{MyName: "Bob"}
	result, err := importer.Import(store, strings.NewReader(chatText))
	if err != nil {
		t.Fatalf("import: %v", err)
	}

	if result.ConversationsCreated != 1 {
		t.Errorf("conversations = %d, want 1", result.ConversationsCreated)
	}
	// 4 messages (media omitted is skipped), multi-line message counts as 1
	if result.MessagesImported != 4 {
		t.Errorf("messages = %d, want 4", result.MessagesImported)
	}

	// Verify conversation name
	convs, _ := store.ListConversationsByPlatform("whatsapp", 10)
	if len(convs) != 1 {
		t.Fatalf("got %d conversations, want 1", len(convs))
	}
	if convs[0].Name != "Alice" {
		t.Errorf("name = %q, want Alice", convs[0].Name)
	}

	// Verify multi-line message was captured correctly
	msgs, _ := store.SearchMessages("check my schedule", "", 10)
	if len(msgs) != 1 {
		t.Fatalf("search for multi-line: got %d, want 1", len(msgs))
	}
	if !strings.Contains(msgs[0].Body, "and get back to you") {
		t.Errorf("multi-line body missing continuation: %q", msgs[0].Body)
	}
}

func TestParseWhatsAppDate(t *testing.T) {
	tests := []struct {
		input string
		zero  bool
	}{
		{"2/10/25, 3:45:22 PM", false},
		{"02/10/2025, 15:45:22", false},
		{"1/1/25, 12:00 AM", false},
		{"invalid", true},
	}
	for _, tt := range tests {
		got := parseWhatsAppDate(tt.input)
		if tt.zero && !got.IsZero() {
			t.Errorf("parseWhatsAppDate(%q) = %v, want zero", tt.input, got)
		}
		if !tt.zero && got.IsZero() {
			t.Errorf("parseWhatsAppDate(%q) = zero, want non-zero", tt.input)
		}
	}
}
