# Game UI

How CraftSurvive builds its page on Rusty Engine. It applies the Engine's game-UI
green path and the conventions rusty-dagger chose
([`docs/game-ui.md`](https://github.com/FuzzySlipper/rusty-dagger/blob/main/docs/game-ui.md)),
fitted to this product. The Engine documents own the Engine rules; this file cites them:

- [Game UI and diagnostic UI](https://github.com/FuzzySlipper/rusty-engine/blob/main/docs/csharp-product-style.md#game-ui-and-diagnostic-ui)
- [Gameplay cursor mode](https://github.com/FuzzySlipper/rusty-engine/blob/main/docs/csharp-product-project.md#gameplay-cursor-mode),
  [Pause and resume from product UI](https://github.com/FuzzySlipper/rusty-engine/blob/main/docs/csharp-product-project.md#pause-and-resume-from-product-ui)
  and [Controller input in product menus](https://github.com/FuzzySlipper/rusty-engine/blob/main/docs/csharp-product-project.md#controller-input-in-product-menus)
- [Desktop shell](https://github.com/FuzzySlipper/rusty-engine/blob/main/docs/desktop-shell.md):
  the window draws the page with Servo.

## Two kinds of UI

**Game UI** is what a player uses: the HUD and hotbar, the pack and journal
(`screens.ts`), the casting ring (`radial.ts`), the town (`town.ts`), the world map
(`world.ts`), the loading screen and the pause menu with its options (`menu.ts`,
`options.ts`). It keeps to the green path and is checked in
`rusty dev --output window` before it is called done, because the window is what
ships. A browser tab is for iterating.

**Diagnostic UI** is for developers and agents. It is the developer tools
(`developer.ts`): the Engine's developer shell docked beside play, outside every
game surface, opened with **`** or the menu's **Developer tools**. Its product
panels are **Craft** (every published fact, the building and landscape-study
shortcuts, the controls) and **Casting** (the development magic compiler, #10529).
With `rusty dev --live-debug` it adds the Engine's console, scene, view and metrics.
Each product panel is marked as diagnostic UI. It may use any browser feature, and
its needs never shape a game surface.

C# owns every game fact; the page draws the projection and claims the product's
intents. It never decides a rule or holds game state.

## Input

**One interaction owner** (`interaction.ts`). A surface that needs the cursor sets
its flag through `layers.open` (`layers.ts`): the pack or journal, the ring, the
menu and the developer tools. While any is open the page is in interface mode
(`context.ui.setInteractionMode('interface')`), which frees the pointer; when the
last closes the page returns to gameplay and calls `context.ui.focusGameplay()`, so
the pointer comes back without a click (in a browser, only on a click or key the
browser counts as a gesture; Escape is not one). The world map and the town are
free-cursor scenes whose clicks reach C# as canvas positions, so they stay in
gameplay with `context.ui.setCursorMode('unlocked')` and return to
`'pointer-lock'` when they close.

**Escape.** Under pointer lock the browser and the desktop shell spend Escape on
ending the lock, and the page sees only the lock end. A lock lost in play with no
surface open is the player asking for the menu, so one press opens it. Escape from
an open surface closes only that surface, innermost first: the developer tools,
then the casting ring, the pack or journal, a town conversation, then the town
itself. In the menu it steps back out of a page, then closes the menu.

**Claims around a lock change.** The Engine keeps a claim the page makes as the
lock is lost or taken back, in the window as in a browser, and delivers it once.
So the menu claims its pause in the lock loss itself, and the ring claims its
composition and returns play the pointer in the same step. The menu still closes
only when the product publishes `menuPaused` as 0, because pausing is C#'s fact.

**Controller.** In play C# reads the pad (Start toggles the menu). While a surface
holds interface mode the pad's buttons reach the page through `context.input`; the
menu takes Start there to step back or continue.

## Building controls

- Each surface is its own module in `src/ui`; `main.ts` only mounts them.
- Build controls once and relabel them from the projection. A press on a node
  replaced under the pointer never becomes a click.
- Servo lacks `:has()`, `::backdrop` and `autofocus`; draw tooltips and keep an
  inline SVG's look in its attributes.
