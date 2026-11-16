# TiXL Web rendering (Phase 1)

Renders HTML / CSS / JavaScript / SVG pages with an off-screen Chromium (CEF) into a TiXL texture.

**Status: written without being able to compile or run it** (the author had no .NET SDK, no Windows and no NuGet access).
Nothing here is verified yet. Expect to fix a few compile errors in the CefSharp calls on the first build.

## Architecture

```
TiXL editor process                          TixlWebHost.exe (separate process, per HtmlRenderer instance)
┌───────────────────────────┐   named pipe   ┌─────────────────────────────────────────┐
│ Operators/Web             │  JSON lines    │ CefSharp.OffScreen (Chromium)           │
│  HtmlRenderer (operator)  │ ─────────────► │  open / reload / resize / fps / pause   │
│  WebHostClient            │ ◄───────────── │  loading, load errors, console, frames  │
│                           │                │                                         │
│  UpdateSubresource ◄──────┼─ shared memory ┼─ OnPaint → BGRA double buffer           │
└───────────────────────────┘  (frames)      └─────────────────────────────────────────┘
```

Why a separate process: TiXL loads each operator package into a collectible `AssemblyLoadContext`
(`Core/Compilation/TixlAssemblyLoadContext.cs`), and CEF can be initialized only once per process, needs native
binaries and a subprocess. In-process CEF would break on operator hot reload. A separate host also isolates browser
crashes from the editor.

Frames reach the GPU through `Texture2D.CreateTexture2D` + `UpdateSubresource` (one CPU copy per frame). A shared D3D11 texture path (`AdoptTexture`, as Spout does) is the planned optimization.

## Install

Requirements: Windows x64, .NET 10 SDK.

1. Apply the TiXL changes (new `Operators/Web` package, `t3.sln` and `Editor/Editor.csproj` entries).
2. Build and install the host (restores CefSharp from NuGet, ~200 MB):
   ```powershell
   .\WebHost\install.ps1
   ```
   It installs to `%LOCALAPPDATA%\TiXL\WebHost`. To use another folder, set the `TIXL_WEBHOST` environment variable
   to the folder or to `TixlWebHost.exe`.
3. Build and start TiXL as usual.

## Try it

1. In TiXL add the operator **HtmlRenderer** (library: `lib.io.web`).
2. Set **Path** to `<repo>\WebHost\examples\hello\index.html` (or a folder that contains an `index.html`).
3. Connect **Texture** to an image view / `RenderTarget` consumer, e.g. `[Layer2d]` or the output view.
4. Toggle **Transparent** to see the page's transparent areas, **Reload** to reload, **Paused** to freeze.

The node's status line shows `Starting browser host...`, `Loading...`, load errors, or a missing-host hint.
Page `console.log` output appears in the TiXL log prefixed with `[web]`. Host logs: `%TEMP%\TixlWebHost`.

## Known limitations (Phase 1)

- Windows only. This port targets TiXL v4.3.0.4 (Direct3D 11 / SharpDX).
- One browser host process per `HtmlRenderer` instance (each is a full Chromium process tree).
- One CPU copy per frame (host → shared memory → GPU). Large resolutions cost bandwidth.
- Alpha is premultiplied as delivered by CEF. Check blending in your graph.
- A page only repaints when it changes; a static page does not produce new frames (the last texture stays).
- Resolution is clamped to 16..8192 px per side.
- No DOM/CSS/JS operators yet (Phases 2-4), no file watching yet (use **Reload**).

## Control protocol (for later phases)

See the header comment of `Program.cs`. Phase 2+ will add `setStyle` / `exec` style commands to the same pipe.

## Time sync (Sync To TiXL Time)

By default the page clock is driven by TiXL's animation time (`LocalFxTime`): the page animates only while TiXL plays
or Idle Motion is on, stands still when TiXL is paused, and follows the timeline when you scrub or loop it.
Turn the input off to let the page run on its own clock. Changing it reloads the page.

What follows TiXL time: `requestAnimationFrame` (callbacks receive TiXL time in ms), `setTimeout`/`setInterval`,
`performance.now()`, `Date.now()`, CSS and Web Animations, SVG (SMIL) animations.
Not covered: `new Date()` without arguments, video/audio playback, iframes.

The script is injected before the page's own scripts (`Page.addScriptToEvaluateOnNewDocument`). The browser still
paints on its own schedule (see **Fps**), so it should be at least as high as TiXL's frame rate. When rendering to a
file, frames are not yet guaranteed to be the page's exact frame for each TiXL frame (a frame handshake is planned).
## DOM and CSS control (Phase 2)

Connect modifier operators to the **Modifiers** input of the HtmlRenderer. They change elements of the loaded page
without reloading it. Only values that changed are sent to the browser, in one batch per frame.

| Operator (library path) | What it does |
|---|---|
| `lib.io.web.dom.ElementSelector` | Builds a CSS selector from a parent, a selector, a relation (descendant, child, same element, next sibling, later siblings) and an optional n-th match. Output: string. |
| `lib.io.web.css.CssProperty` | Sets any CSS property (`element.style.setProperty`) from text. Works for custom properties such as `--hue`. Empty value removes the override. |
| `lib.io.web.css.CssNumber` | Sets a property to a float plus unit (for example `rotate` in `deg`, `opacity`, `--hue`). Connect LFOs, audio levels, envelopes. |
| `lib.io.web.css.CssColor` | Sets a color property (`fill`, `stroke`, `color`, `background-color`, ...) from a TiXL color. |
| `lib.io.web.dom.ElementAttribute` | Sets or removes an attribute (`class`, `data-*`, SVG attributes such as `d`). |
| `lib.io.web.dom.ElementVisibility` | Shows or hides elements (`visibility` or `display`). Visible removes the override. |

The **Selector** input of every operator takes plain CSS selectors typed in, or the output of an ElementSelector.
All matching elements are changed. Examples: `#logo`, `.title`, `svg path`, `#visualizer .bar`, `#spinner > path`.
Elements that appear or disappear later are found automatically. If a selector matches nothing, or is invalid, the
HtmlRenderer shows a warning: `No element matches: [...]`.

When a modifier operator is disconnected, its change is undone (the style or attribute is removed again).
The changes are applied again after every page reload.

### Try it with `examples/dom-control`

1. Set the HtmlRenderer **Path** to `WebHost\examples\dom-control\index.html`.
2. Color one path: add `CssColor`, **Selector** `#spinner path:nth-of-type(2)` (or `ElementSelector` with ParentSelector `#spinner`,
   Selector `path`, Nth `2`), **Property** `fill`, pick a color. Connect its output to the HtmlRenderer **Modifiers** input.
3. Rotate with an LFO: add `CssNumber`, **Selector** `#spinner`, **Property** `rotate`, **Unit** `deg`. Connect an LFO
   (scaled to 0..360) to **Value**, and the output to **Modifiers**. The page example sets `transform-box: fill-box`, so
   SVG shapes rotate around their own center.
4. Audio to opacity: add `CssNumber`, **Selector** `#logo`, **Property** `opacity`, connect an audio level (0..1) to **Value**.
5. Hide the title: add `ElementVisibility`, **Selector** `#title`, switch **Visible** off.

Notes:
- `rotate`, `scale` and `translate` are CSS individual transform properties (Chromium 104 and newer). A full
  `transform` value can be set with `CssProperty`.
- A style set by TiXL wins over the page's stylesheet rules, but not over `!important` rules or running CSS animations.
- To undo a change, disconnect the operator or clear its value in `CssProperty`.
