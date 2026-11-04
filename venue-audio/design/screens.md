# Venue Audio: screen inventory for layout exploration

Venue Audio is an FFXIV plugin (it runs inside the game through Dalamud). A **host** streams their venue's
music live to **listeners**, who join with a six-character code or by walking into the host's house. Each
window below floats over the game. This file lists every screen, every state, and every piece of text as
shipped in **0.3.0**, so new layouts can be explored without missing anything.

## What the UI can and can't do (Dear ImGui)

Layouts have to be buildable in immediate-mode ImGui. In practice:

- **Can:**
  - Rectangles (any corner radius), circles, lines, and text in one font size (the game's UI font).
  - A monospace font for codes and timers.
  - FontAwesome 5 icons as glyphs.
  - Standard widgets: button, checkbox, slider, dropdown (combo), text input, collapsible header, and
    tooltips on hover.
  - Per-frame animation (pulses, glows, fades, slides). Pages already slide sideways and windows ease
    their height.
  - Two-colour linear gradients on rectangles.
- **Awkward:** more than one text size (a second font atom would need loading), wrapped text inside
  custom-drawn shapes, and overlapping interactive elements.
- **Can't:** images other than the plugin icon, blur or drop shadows (faked with stacked translucent
  rects), arbitrary fonts, or CSS-style flex layouts. Everything flows top to bottom unless it's placed
  manually.
- Windows have a title bar ("Venue Audio") with title-bar icon buttons on the right.
- The **main window's height fits its content automatically**. Its width is user-draggable, at least 420 px,
  460 px by default.
- UI scale varies (the game's global scale), so sizes below are at 100%.

## Styles (palettes)

The palette button in the title bar cycles these styles. A design should work with the slots, not one
fixed colour set.

| Slot | Velvet (default) | Midnight | Gilded | Moonstone |
| --- | --- | --- | --- | --- |
| Window ground | `#161617` | `#0d0f1c` | `#14110c` | `#17151d` |
| Panels/cards | `#1d1d20` | `#151933` | `#1d1912` | `#1f1c28` |
| Input fields | `#2a2a2c` | `#1d2240` | `#28221a` | `#2a2635` |
| Raised buttons | `#3d3d40` | `#2a3058` | `#3a3224` | `#3a3549` |
| Edges | `#3a3a3e` | `#323a66` | `#4a3f2b` | `#413b52` |
| Ink (text) | `#f4f2ef` | `#eef0ff` | `#f6eedd` | `#f3f0fa` |
| Muted text | `#a9a6a1` | `#9aa0c8` | `#b5a88e` | `#aaa3bd` |
| Title bar | `#5e2222` | `#3b1a5c` | `#4a3512` | `#3d2f5e` |
| **Host accent** | `#d94141` crimson | `#ff4fa3` hot pink | `#d6a84a` gold | `#b18cff` violet |
| Text on host accent | white | white | `#1a1408` | `#160f26` |
| **Listener accent** | `#4ea1d9` blue | `#3ad6f0` cyan | `#6fb7a8` jade | `#6ee7c8` mint |
| Error / warning | `#ff8a8a` / `#e0b84a` | `#ff7a9a` / `#ffd166` | `#ff8f7a` / `#e8c46a` | `#ff9db0` / `#f2d27a` |
| Corner roundness | 1× | 1.6× | 0.5× | 2.2× |

Badges, hovers and glows are mixed from these. A fifth style, "Dalamud", borrows the player's own Dalamud
theme. Level meters use fixed green `#5fbf7a`, amber `#e0b84a` and red `#e05a5a`.

The plugin icon is pixel-art crimson headphones around an ivory music note, on a transparent background.

---

## 1. Main window, role picker (first screen)

- Title bar: "Venue Audio", plus title-bar buttons **?** (opens the guide) and **palette** (next style;
  right-click goes to the previous one).
- Two large clickable cards, stacked full width, each 84 px tall:
  - **Host**: a circular badge in the host accent with a broadcast-tower icon, the title "Host", the subtitle
    "Broadcast your venue's music to guests", and a chevron on the right.
  - **Listener**: a circular badge in the listener accent with a headphones icon, the title "Listener", the
    subtitle "Tune in with a venue code, or walk in", and a chevron.
- Nothing else. The choice is remembered, so returning players land on their last screen.

## 2. Main window, Host screen

Top to bottom:

1. **Header row:** a pill badge "HOST" (host accent) on the left, and a "Switch role" text link on the
   right. The link is disabled while live or busy.
2. **The broadcast button:** full width, 56 px tall, a dot plus a label, centred. There are four states:
   - Idle: an outlined host-accent button with a tinted fill, "● Start broadcasting". It is disabled until
     an audio source exists, and then shows the muted text "Choose an audio source to start." below it.
     After a failure, the error text appears below it instead.
   - Going live: "● Going live…  ·  Cancel". The status text below steps through "Starting broadcast…",
     "Opening a temporary internet relay… This can take about a minute." and "Broadcasting. Reserving a
     short code…".
   - **Live:** solid host accent with a pulsing glow (three stacked translucent rings that breathe), a
     pulsing white dot, "LIVE  ·  Stop broadcasting", and a monospace timer "00:12:34" in the button.
     Below, in the host-accent text colour: "You are on air. 12 listening." It reads "On air, but
     everything is muted." when every channel is muted.
   - Stopping: "● Stopping…" (disabled), with the status text below.
3. **Venue code (only while live):**
   - The label "VENUE CODE".
   - A field-coloured box showing the code in monospace, e.g. `K3M-9QX`, or `•••-•••` when hidden. To its
     right: an eye/eye-slash icon button (hide the code on stream) and a "⧉ Copy" button that briefly reads
     "Copied".
   - A text link: "Copy the full invite instead" (it briefly reads "Full invite copied").
   - An optional warning line (warning colour), e.g. "Couldn't refresh the short code; retrying." or
     "The short code expired. Share the full invite instead."
   - **Fallback state:** when the code service is down, the label reads "GUEST INVITE" with a read-only
     monospace field holding a long URL plus Copy.
4. **The label "VENUE NAME"**, with a full-width text input hinted "[Your venue's name]" (max 48 characters).
   It is locked while live.
5. **Checkbox "Announce broadcast".** It is disabled unless the host is standing inside a house. Hovering
   shows "Stand inside your venue's house to announce your broadcast there." or, when inside, the location,
   e.g. "Ward 12, plot 30" or "Ward 3, apartment 41".
6. **The label "AUDIO":**
   - A source dropdown ("Output: Speakers…" / "Input: Stream Mix…") plus a square refresh-icon button.
     Locked while live.
   - "Level": a 16-segment horizontal meter, green, then amber, then red.
   - "Music": a 0–100% slider plus a square mute toggle (volume icon / muted icon in red). Usable while live.
7. **The label "MICROPHONE":**
   - A dropdown, first entry **None** (the default), then input devices.
   - Only when a mic is picked: a "Mic" slider plus a mute toggle (microphone / microphone-slash icon).
8. **The label "SOUND QUALITY":** a dropdown with "Voice · 64 kbps", "Standard · 96 kbps", "High · 128 kbps"
   (the default) and "Studio · 192 kbps". Locked while live.
9. **Checkbox "Show a floating live badge"** (on by default).

Section labels are small, muted and all-caps. Sliders share a label column about 56 px wide.

## 3. Main window, Listener screen

**Not connected**, top to bottom:

1. **Header row:** a pill "LISTENER" (listener accent) and the "Switch role" link.
2. **The label "VENUE CODE OR INVITE"**, then a monospace text input hinted `K3M-9QX` plus a "Paste" button.
3. **The button "Join venue"**: full width, 44 px, filled with the listener accent. It is disabled while the
   input is empty. Error text, e.g. "Could not continue: No venue is using that code right now. Check it
   with the host.", can appear below it.
4. **"Volume":** a slider plus a mute toggle (listener-accent grab).
5. **The label "WALK-IN VENUES"**, then two checkboxes:
   - "Auto Discover Venues" (on by default). Hover: "Walking into a house with an announced broadcast
     automatically asks you to join the venue's listening party."
   - "Auto Join Venues" (indented feel, disabled unless Discover is on). Hover: "Skips the pop-up and
     joins the venue's listening party as soon as you enter."

**Connecting / disconnecting:**
- A "now playing" card (see below) captioned "CONNECTING" or "DISCONNECTING".
- The status text.
- A full-width "Cancel" button.

**Listening:**
- The **now playing card**: full width, 74 px, with a listener-tinted fill and edge. On the left, a pulsing
  listener-accent dot. Three lines of text:
  - "NOW LISTENING" (accent ink)
  - the venue name (or "Live venue")
  - "Hosted by Firstname Lastname" (muted)
- On the right of the card, a monospace elapsed timer.
- "Signal": the 16-segment meter.
- "Volume": a slider plus mute.
- A full-width "Disconnect" button (neutral raised colour).

## 4. Floating live badge (host, while live)

This is a separate small window with no title bar, auto-sized, that the player can drag anywhere. It has
6 px vertical padding, extra-rounded corners and a host-accent border. In one row:

- a pulsing host-accent dot
- "LIVE" (host ink)
- a monospace timer "00:12:34"
- a headphones icon, then the listener count "12"
- the monospace code `K3M-9QX` (omitted when the host hid it)
- a mute-music icon button (red when muted)
- a sliders icon button that opens the main window

## 5. Join prompt (listener walks into an announced house)

- A separate auto-sized window titled "Venue Audio", placed upper-centre of the screen.
- A circular listener badge with a music icon, and the heading "This venue has Venue Audio".
- Body (wraps at 360 px): "{Venue name} is playing live music, hosted by {Host}. Would you like to join
  and listen?"
- Two half-width buttons: **Join** (listener accent) and **Not now** (neutral).
- A checkbox: "Join walk-in venues automatically from now on".
- Closing it counts as "Not now", and that house isn't offered again until the plugin reloads.

## 6. Guide (first open, and from the ? button)

- A separate window, 400 px content width.
- Each page has a header (a 40 px circular badge with an icon, plus the title), numbered steps (accent
  numbers), then bullet tips (muted dots, body text).
- A pinned footer: page dots on the left, **Back** / **Next** on the right. On the last page, Next becomes
  **Done**, filled with the host accent.
- Pages slide sideways, and the window eases its height to each page.
- It opens on the page for the current role.

**Welcome** (neutral badge, music icon):
1. Hosts stream their venue's music live to everyone listening.
2. Pick Host if you run the music, or Listener if you're tuning in.
3. Switch role, top right, takes you back to that choice anytime.

**How to host** (host badge, tower icon):
1. Choose the audio source your music plays through.
2. Press Start broadcasting. Going live can take up to a minute.
3. Share your six-character code with guests. You get a new one each time you go live.

Tips:
- Picking your speakers or headset sends everything you hear, game sounds included.
- To send only music, give it a device of its own: a virtual cable (VB-Audio Virtual Cable), a virtual mixer
  (VoiceMeeter), or a stream mix from an app like Elgato Wave Link or SteelSeries Sonar. Windows' Volume
  mixer can send just your music player to that device.
- Pick a microphone only if you want your voice in the stream and your mixer doesn't already carry it.
- Announce broadcast: stand inside your house before going live. Anyone with Venue Audio who walks in is
  offered the music, no code needed.
- Every guest is a separate copy of the stream on your upload. High quality is about 180 kbps per guest,
  so 50 guests need about 9 Mbps.
- The eye button hides your code while you stream. Set in-game booth music to Off.

**How to listen** (listener badge, headphones icon):
1. Type or paste the host's code.
2. Press Join venue.

Tips: the Auto Discover and Auto Join explanations above, plus "If you joined by walking in, leaving the
house disconnects you."

**Good to know** (neutral badge, lightbulb icon), tips only:
- While you're live, a small badge floats on screen. Drag it anywhere. Its buttons mute your music or open
  these controls.
- The palette button in the title bar changes the style. Right-click goes back one.
- The question mark reopens this guide. /venueaudio opens the window.

---

## Known pain points worth exploring

- The Host screen is long: audio, mic and quality all stack under the broadcast button. Could setup
  collapse, use tabs, or move to a separate "setup" step once configured?
- While live, the host mostly needs the code, the mutes, the level and the listener count. The rest is
  locked and could fold away.
- The role picker is just two cards. It could show more personality (the icon, the style).
- The listener screen has plenty of room that it doesn't use.
- There's no venue or host identity on the host side while live (what guests see).
