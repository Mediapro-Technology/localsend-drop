---
id: T-019
title: Received messages can be selected and copied
spec: none
status: done
depends: []
files: [IncomingCard.cs, NotesForm.cs, Theme.cs, Snapshot.cs]
tdd: none
done: 2026-11-25
no_tests: UI-only: WinForms TextBox selection and painted copy icon; verified with --snapshot renders in all 3 looks
---
## Goal
A message from another PC was painted onto the card, so its text couldn't be selected.
Show it in a read-only text box (select, Ctrl+C, right-click Copy) with a small copy icon beside it.

## Steps
1. Message text goes in a borderless read-only multiline TextBox, wrapped, up to 6 lines then scrolls.
2. Clicking in it activates the card so Ctrl+C works (the card is otherwise no-activate).
3. A copy icon (Segoe Fluent E8C8) right of the text copies it all and shows a check for 1.5 s. On Glass the box sits in a solid bubble (a TextBox cannot be see-through).
4. Conversation window bubbles: hovering one shows a copy icon beside it; right-click gives Copy.

## Done when
- [ ] `lean test` passes
- [ ] Builds; message card shows selectable text and the copy icon
