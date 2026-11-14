# Update check

## Endpoint

`https://api.github.com/repos/lso2/MultiDesk/releases/latest`, which answers with the newest
published, non-prerelease release. The button and the settings link both open
`https://github.com/lso2/MultiDesk/releases`.

The request sends a user-agent header carrying the running version. The API rejects a request without
one. TLS 1.2 is added to the allowed protocols explicitly.

## Schedule

A check runs 12 seconds after launch and once every 24 hours after that, both gated on the
**Check for new versions automatically** setting. The **Check now** button in settings runs one
whatever that setting says. The request runs off the interface thread and the answer is raised back
on it.

Turning the setting on asks for an answer immediately. Turning it off clears the bar button. Only an
actual move of that setting counts: `SettingsStore.Save` raises `Changed` on every save, and
`DesktopManager` saves window placements every 45 seconds, so reacting to the event alone would have
meant a request every 45 seconds, past GitHub's unauthenticated limit of 60 an hour within the first
hour.

Scheduled checks also carry a floor of one hour since the last one. **Check now** ignores the floor,
since the person asked for it by hand.

## Tag parsing

This project publishes release tags in more than one shape. The latest at the time of writing is
`MultiDeskSetup-2.9.11-release`.

| Tag | Parsed |
| --- | --- |
| `2.10.0` | 2.10.0 |
| `v2.9.17` | 2.9.17 |
| `MultiDeskSetup-2.9.11-release` | 2.9.11 |
| `MultiDesk2-1.0.3` | 1.0.3 |
| `release-7` | 7.0 |
| `nightly` | nothing |
| `1.2.3.4.5` | 1.2.3.4 |

The rule is the first run of digits that carries a dot, falling back to the last run of digits when
none does. A name containing its own number is therefore not read as the version. A tag with more
than 4 parts is cut to 4, which is the most `System.Version` accepts.

The comparison is numeric through `System.Version`, so 2.10.0 is newer than 2.9.17.

## Answers that are not failures

A 404 means the release could not be found, which the service reads as no update and records as
`UPD no-release`. Every other web failure is recorded as `UPD check-failed` with its status, and the
button stays hidden.

## The bar button

The button sits directly above the coffee row. A `DockPanel` gives the bottom edge to whichever
bottom-docked child is declared first, so the update button is declared after the coffee button and
stacks above it. It shows only on a vertical bar, the same rule the coffee row follows, and only once
a newer release has been read. Its tooltip names the version.

The button uses the `AccentButton` style: the accent color with white text and no border, dimmed on
hover and press by opacity alone. The glyph is `E896` from Segoe MDL2 Assets.

## Verification performed

Run on 2026-09-14 against the live API, from a console harness compiled from these same sources. The
7 tag shapes above, the live request, and the 404 path were all executed. The live answer was
`MultiDeskSetup-2.9.11-release`, parsed as 2.9.11, compared against 2.10.0, verdict no update. That
run is what found the installer-style tag, which the first version of the parser rejected.

The schedule was driven the same way: 20 unrelated settings saves asked for no check, turning the
setting off asked for none, turning it back on asked for exactly one, a scheduled check inside the
hour was refused, and one past the hour ran.
