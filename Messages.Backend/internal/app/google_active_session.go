package app

import (
	"errors"
	"time"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// Google Messages delivers live updates to one linked web session at a time.
// When another session (another PC, or messages.google.com) becomes active,
// Google sends this session a BROWSER_INACTIVE user alert and stops routing
// messages here while the long-poll — and so Connected — stays up. Without
// handling it, the app looks connected but silently goes stale.

// googleActivateMinInterval paces SetActiveSession calls; Google throttles
// accounts whose sessions keep reconnecting.
const googleActivateMinInterval = time.Minute

var errGoogleActivateTooSoon = errors.New("this device took over Google Messages less than a minute ago; try again shortly")

// GoogleSessionInactive reports whether Google last told this session it is
// no longer the active one.
func (a *App) GoogleSessionInactive() bool {
	return a.googleSessionInactive.Load()
}

// BrowserInactive records that Google deactivated this session. A timeout or
// inactivity deactivation involves no other device, so the session is taken
// back right away; a takeover by another device is left for the user (or the
// client app, when it is in use) to reclaim, so two PCs don't keep stealing
// the session from each other.
func (a *App) BrowserInactive(alert gmproto.AlertType) {
	a.googleSessionInactive.Store(true)
	a.Logger.Warn().Str("alert", alert.String()).Msg("Google Messages made this session inactive")
	a.emitStatusChange(a.Connected.Load())
	if alert != gmproto.AlertType_BROWSER_INACTIVE_FROM_TIMEOUT && alert != gmproto.AlertType_BROWSER_INACTIVE_FROM_INACTIVITY {
		return
	}
	go func() {
		time.Sleep(5 * time.Second)
		if err := a.ActivateGoogleSession(); err != nil {
			a.Logger.Warn().Err(err).Msg("Could not reactivate the Google Messages session")
		}
	}()
}

// ActivateGoogleSession makes this the active Google Messages session again
// (the web client's "Use here") and catches up on anything missed meanwhile.
func (a *App) ActivateGoogleSession() error {
	now := time.Now().UnixMilli()
	if last := a.googleLastActivateMS.Load(); last > 0 && now-last < googleActivateMinInterval.Milliseconds() {
		return errGoogleActivateTooSoon
	}
	cli := a.GetClient()
	if cli == nil || cli.GM == nil {
		return errors.New(ErrNotConnected)
	}
	a.googleLastActivateMS.Store(now)
	if err := cli.GM.SetActiveSession(); err != nil {
		return err
	}
	a.googleSessionInactive.Store(false)
	a.Logger.Info().Msg("Google Messages session is active on this device again")
	a.emitStatusChange(a.Connected.Load())
	a.StartRecentReconcile("session_reactivated")
	return nil
}
