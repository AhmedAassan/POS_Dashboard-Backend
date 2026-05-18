import { RoleColumns, RoleRow, RoleService } from "@/ServerTypes/Administration";
import { Decorators, EntityGrid } from "@serenity-is/corelib";
import { RoleDialog } from "./RoleDialog";

@Decorators.registerClass('PosDashboard.Administration.RoleGrid')
export class RoleGrid extends EntityGrid<RoleRow, any> {
    protected getColumnsKey() { return RoleColumns.columnsKey; }
    protected getDialogType() { return RoleDialog; }
    protected getRowDefinition() { return RoleRow; }
    protected getService() { return RoleService.baseUrl; }

    constructor(container: JQuery) {
        super(container);
    }

    protected getDefaultSortBy() {
        return [RoleRow.Fields.RoleName];
    }
}