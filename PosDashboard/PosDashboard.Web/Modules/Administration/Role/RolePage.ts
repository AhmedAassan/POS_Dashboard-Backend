import { initFullHeightGridPage } from "@serenity-is/corelib/q"
import { RoleGrid } from "./RoleGrid";

export default function pageInit() {
    initFullHeightGridPage(new RoleGrid($('#GridDiv')).element);
}