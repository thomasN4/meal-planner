// The single owner of the app's language preference.
//
// A sibling of theme.js rather than an addition to it: that file is the single
// owner of the *colour theme*, and bolting a second unrelated global onto it
// would make that claim false and put a second MODES array one typo away from
// being read by the wrong indexOf.
//
// Deferred, unlike theme.js, because this paints nothing. theme.js is blocking
// in <head> so data-bs-theme lands before the first paint; there is no
// equivalent here yet, and see below for why.
//
// There is deliberately no apply(). Nothing is translated: every screen in this
// app is English, and <html lang="en"> in App.razor stays that way. Stamping
// lang="fr" over English text would make a French screen-reader voice read
// English words — an accessibility regression dressed up as progress, and the
// same lie the settings page's "not yet in effect" banner exists to prevent,
// aimed at assistive technology instead of at the reader. A named no-op apply()
// would only invite someone to "fix" it by making it do something.
//
// What the localization pass adds here, when it comes: apply() stamping
// <html lang>, a Blazor 'enhancedload' listener to put the attribute back after
// enhanced navigation strips it (theme.js documents that trap), a move up to
// blocking-in-head, and — the part easy to miss — mirroring the preference into
// an .AspNetCore.Culture cookie, because UseRequestLocalization runs on the HTTP
// request that starts the circuit and localStorage is invisible there.
(function () {
    'use strict';

    var KEY = 'mealplanner.lang';
    var MODES = ['system', 'en', 'fr'];

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

    window.mealPlannerLang = {
        get: read,

        set: function (mode) {
            // Guarded here as well as in Settings.razor, not instead of it:
            // localStorage is user-writable and either end can be reached first.
            if (MODES.indexOf(mode) < 0) {
                mode = 'system';
            }

            try {
                window.localStorage.setItem(KEY, mode);
            } catch (e) {
                // Ignore: nothing reads this yet, so there is nothing to break.
            }

            return mode;
        }
    };
})();
