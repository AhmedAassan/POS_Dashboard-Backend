import { initFullHeightGridPage } from "@serenity-is/corelib/q"
import { LanguageGrid } from "./LanguageGrid";

export default function pageInit() {
    initFullHeightGridPage(new LanguageGrid($('#GridDiv')).element);
}