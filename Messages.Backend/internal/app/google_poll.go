package app

import (
	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// pollConversationLimit is how many of the newest conversations a poll compares.
const pollConversationLimit = 15

// PollRecentGoogle asks the phone for its newest conversations and fetches
// messages only for those whose last message is newer than the stored copy.
//
// Google pushes live messages to one linked session at a time, but requests from
// a session that isn't the active one are still answered. Polling keeps every
// linked PC current without taking live delivery away from the PC in use. It is
// cheap when nothing changed: one list call, no per-conversation fetches.
// Returns false when a backfill or reconcile is already running.
func (a *App) PollRecentGoogle() bool {
	if a.backfillRunning.Load() || !a.reconcileRunning.CompareAndSwap(false, true) {
		return false
	}
	go func() {
		defer a.reconcileRunning.Store(false)
		catchUp := a.beginGoogleCatchUp("poll")
		if catchUp == nil {
			return
		}
		defer catchUp.finish()

		resp, err := catchUp.gm.ListConversationsWithCursor(pollConversationLimit, gmproto.ListConversationsRequest_INBOX, nil)
		a.recordGoogleListPull(catchUp.token, "poll", gmproto.ListConversationsRequest_INBOX, true, resp, err)
		if err != nil {
			if a.HandleGoogleAuthExpiredError(err) {
				return
			}
			a.Logger.Debug().Err(err).Msg("Poll: list conversations failed")
			return
		}

		var newer int
		for _, conv := range resp.GetConversations() {
			if !catchUp.stillCurrent() {
				return
			}
			remoteMS := conv.GetLastMessageTimestamp() / 1000
			if local, err := a.Store.GetMessagesByConversation(conv.GetConversationID(), 1); err == nil &&
				len(local) > 0 && local[0].TimestampMS >= remoteMS {
				continue
			}
			if err := catchUp.storeConversation(conv); err != nil {
				a.Logger.Warn().Err(err).Str("conv_id", conv.GetConversationID()).Msg("Poll: store conversation failed")
			}
			stored, aborted := a.reconcileRecentConversationMessages(catchUp, conv)
			if aborted {
				return
			}
			if stored {
				newer++
			}
		}
		if newer > 0 {
			a.Logger.Info().Int("conversations", newer).Msg("Poll found new messages")
			a.emitConversationsChange()
			a.emitMessagesChange("")
		}
	}()
	return true
}
