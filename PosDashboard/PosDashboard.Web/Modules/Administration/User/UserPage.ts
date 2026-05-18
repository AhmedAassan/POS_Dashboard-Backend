import { initFullHeightGridPage } from "@serenity-is/corelib/q"
import { UserGrid } from "./UserGrid";

export default function pageInit() {
    initFullHeightGridPage(new UserGrid($('#GridDiv')).element);
}