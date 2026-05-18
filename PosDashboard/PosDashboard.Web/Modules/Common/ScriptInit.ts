import { EntityDialog } from "@serenity-is/corelib";
import { gridDefaults } from "@serenity-is/sleekgrid";
import { Authorization, Config, ErrorHandling } from "@serenity-is/corelib/q";
import { IdleTimeout } from "@serenity-is/pro.extensions";
import { siteLanguageList } from "./Helpers/LanguageList";

Config.rootNamespaces.push('PosDashboard');
siteLanguageList().then(value => EntityDialog.defaultLanguageList = () => value);
gridDefaults.useCssVars = true;

if ($.fn['colorbox']) {
    $.fn['colorbox'].settings.maxWidth = "95%";
    $.fn['colorbox'].settings.maxHeight = "95%";
}

window.onerror = ErrorHandling.runtimeErrorHandler;

$(() => {
    // let demo page use its own settings for idle timeout
    if (window.location.pathname.indexOf('Samples/IdleTimeout') > 0)
        return;

    var meta = $('meta[name=username]');
    if ((meta.length && meta.attr('content')) ||
        (!meta.length && Authorization.isLoggedIn)) {

        new IdleTimeout({
            activityTimeout: 15 * 60,
            warningDuration: 2 * 60
        });
    }
});