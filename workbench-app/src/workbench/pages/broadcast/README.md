# Broadcast Page

The broadcast page is split into a page shell, controller, helpers, and focused components.

- `BroadcastPage.jsx` mounts the controller and layout.
- `useBroadcastController.js` owns page orchestration and returns the page model.
- `useBroadcastPlatformRules.js`, `useBroadcastStationBindings.js`, and `useBroadcastAssets.js` own platform announcements, station bindings, and asset browser/preview operations.
- `broadcast-*.js` files hold constants, normalization, assets, bindings, rules, preview, and view-model helpers.
- `components/` renders toolbar, rule lists, mapping, asset tray/sidebar, asset explorer, animations, icons, and preview volume controls.

The legacy root `src/BroadcastWorkbenchPage.jsx` only re-exports this page.
