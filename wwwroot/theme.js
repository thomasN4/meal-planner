// The single owner of the app's colour theme.
//
// Bootstrap 5.3 keys its dark palette off [data-bs-theme=dark] on <html>, not
// off a prefers-color-scheme media query, so resolving "follow the system"
// needs script — there is no CSS-only spelling of it. Everything about the
// theme lives here; ThemeToggle.razor is a view over this file and never
// decides the theme itself.
//
// This script is loaded blocking from <head> (App.razor), before the
// stylesheets, so the attribute is on <html> before the first paint. Deferring
// it, or moving it into a Blazor component that runs once the circuit connects,
// is what produces a white flash on every load.
(function () {
    'use strict';

    var KEY = 'mealplanner.theme';
    var MODES = ['system', 'light', 'dark'];
    var dark = window.matchMedia('(prefers-color-scheme: dark)');

    function read() {
        var stored;
        try {
            stored = window.localStorage.getItem(KEY);
        } catch (e) {
            // Private browsing, or storage disabled. Following the system is a
            // fine answer to "we cannot remember what you asked for".
            return 'system';
        }

        return MODES.indexOf(stored) >= 0 ? stored : 'system';
    }

    function apply() {
        var mode = read();
        var resolved = mode === 'system' ? (dark.matches ? 'dark' : 'light') : mode;

        // Set it for light too, rather than removing the attribute. Bootstrap's
        // :root is already light so it changes nothing visually, but it means
        // the resolved theme is always readable off the DOM instead of being
        // "dark, or else absent".
        document.documentElement.setAttribute('data-bs-theme', resolved);
    }

    window.mealPlannerTheme = {
        get: read,

        set: function (mode) {
            if (MODES.indexOf(mode) < 0) {
                mode = 'system';
            }

            try {
                window.localStorage.setItem(KEY, mode);
            } catch (e) {
                // Ignore: the theme still applies for this page's lifetime.
            }

            apply();
            return mode;
        }
    };

    apply();

    // Live-follow the OS while the mode is "system". apply() re-reads the mode
    // every time, so this is harmless when the user has picked explicitly.
    dark.addEventListener('change', apply);

    // Blazor's enhanced navigation re-synchronises <html>'s attributes against
    // the markup the server just sent, and the server sends no data-bs-theme —
    // so without this, clicking a NavLink strips the attribute and snaps the
    // page back to light. blazor.web.js is the last element in <body> and has
    // therefore run by DOMContentLoaded; the optional chaining covers the case
    // where it has not.
    document.addEventListener('DOMContentLoaded', function () {
        if (window.Blazor && window.Blazor.addEventListener) {
            window.Blazor.addEventListener('enhancedload', apply);
        }
    });
})();
