package client

import (
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// Google keeps the long-poll open after another session takes over, so the only
// signal that live messages stopped is this user alert. It must reach the app.
func TestBrowserInactiveAlertReachesApp(t *testing.T) {
	for _, alert := range []gmproto.AlertType{
		gmproto.AlertType_BROWSER_INACTIVE,
		gmproto.AlertType_BROWSER_INACTIVE_FROM_TIMEOUT,
		gmproto.AlertType_BROWSER_INACTIVE_FROM_INACTIVITY,
	} {
		var got []gmproto.AlertType
		h := &EventHandler{Logger: zerolog.Nop(), OnBrowserInactive: func(a gmproto.AlertType) { got = append(got, a) }}
		h.Handle(&gmproto.UserAlertEvent{AlertType: alert})
		if len(got) != 1 || got[0] != alert {
			t.Fatalf("alert %v: callback got %v", alert, got)
		}
	}
}

func TestOtherUserAlertsAreIgnored(t *testing.T) {
	called := false
	h := &EventHandler{Logger: zerolog.Nop(), OnBrowserInactive: func(gmproto.AlertType) { called = true }}
	h.Handle(&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_MOBILE_BATTERY_LOW})
	h.Handle(&gmproto.UserAlertEvent{AlertType: gmproto.AlertType_BROWSER_ACTIVE})
	if called {
		t.Fatal("non-inactive alerts must not mark the session inactive")
	}
}
