import { RoleForm, RoleRow, RoleService } from "@/ServerTypes/Administration";
import { Texts } from "@/ServerTypes/Texts";
import { Decorators, EntityDialog } from "@serenity-is/corelib";
import { RolePermissionDialog } from "../RolePermission/RolePermissionDialog";

const editPermissions = "edit-permissions";

@Decorators.registerClass('PosDashboard.Administration.RoleDialog')
export class RoleDialog extends EntityDialog<RoleRow, any> {
    protected getFormKey() { return RoleForm.formKey; }
    protected getRowDefinition() { return RoleRow; }
    protected getService() { return RoleService.baseUrl; }

    protected form = new RoleForm(this.idPrefix);

    protected getToolbarButtons()
    {
        let buttons = super.getToolbarButtons();

        buttons.push({
            title: Texts.Site.RolePermissionDialog.EditButton,
            cssClass: editPermissions,
            icon: 'fa-lock text-green',
            onClick: () =>
            {
                new RolePermissionDialog({
                    roleID: this.entity.RoleId,
                    title: this.entity.RoleName
                }).dialogOpen();
            }
        });

        return buttons;
    }

    protected updateInterface() {
        super.updateInterface();

        this.toolbar.findButton(editPermissions).toggleClass("disabled", this.isNewOrDeleted());
    }
}