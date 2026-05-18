import { ScriptUserDefinition } from "@/ServerTypes/ScriptUserDefinition";
import { getRemoteData } from "@serenity-is/corelib/q";

export function userDefinition() {
    return getRemoteData('UserData') as ScriptUserDefinition;
}

export function hasPermission(permissionKey: string): boolean {
    let ud = userDefinition();
    return ud.Username === 'admin' || !!ud.Permissions[permissionKey];
}