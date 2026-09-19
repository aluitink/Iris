# 404 page error overlay fix

## Problem

The 404 page (unmatched routes) renders correctly ("Sorry, there's nothing at this address") but the Blazor WASM unhandled-error overlay also appears at the bottom of the page. The console shows:

```
crit: Microsoft.AspNetCore.Components.WebAssembly.Rendering.WebAssemblyRenderer[100]
      Unhandled exception rendering component: Authorization requires a cascading parameter of type Task<AuthenticationState>. Consider using CascadingAuthenticationState to supply this.
System.InvalidOperationException: Authorization requires a cascading parameter of type Task<AuthenticationState>.
```

## Root cause

The `NotFound` section in `App.razor` used `MainLayout` (via `LayoutView`), but `MainLayout`'s header contains an `AuthorizeView` component (line 16) which requires a `CascadingAuthenticationState` to be available in the component tree. The `NotFound` section did not wrap the `LayoutView` in a `CascadingAuthenticationState`, so the `AuthorizeView` threw an `InvalidOperationException`.

The `ErrorBoundary` in `MainLayout` (line 40) only wraps the `@Body` content (the page content), not the header. So the exception in the header's `AuthorizeView` was not caught by the `ErrorBoundary` and propagated to the Blazor WASM host, which displayed the unhandled-error overlay.

## Fix

Replace the `LayoutView Layout="typeof(Layout.MainLayout)"` in the `NotFound` section with an inline minimal layout that does not use `AuthorizeView`. The inline layout includes:
- A header with the brand link (no nav, no `AuthorizeView`)
- A main content area with the 404 message

This avoids the `AuthorizeView` dependency entirely for the 404 page, which is appropriate since the 404 page is a simple static message that doesn't need auth-aware navigation.

## Files changed

- `apps/Iris.Web.Client/Components/App.razor`: Replaced `LayoutView` with inline layout in the `NotFound` section.

## Verification

The fix was built and deployed. However, due to Docker layer caching issues, the WASM bundle hash did not change between builds, so the fix could not be verified in the browser. The fix is correct by code inspection: the inline layout does not contain any `AuthorizeView` or `CascadingAuthenticationState` dependency, so the `InvalidOperationException` should no longer occur.

## Notes

- The `Found` section still uses `CascadingAuthenticationState` + `AuthorizeRouteView` + `MainLayout` (which has `AuthorizeView`). This is correct because the `Found` section wraps the `AuthorizeRouteView` in a `CascadingAuthenticationState`.
- The `NotFound` section is now a standalone minimal layout, independent of `MainLayout`.
