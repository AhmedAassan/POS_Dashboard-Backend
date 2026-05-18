import { LoginRequest } from "./Modules.System.Models.LoginRequest";
import { ActionResult } from "../Microsoft/AspNetCore.Mvc.ActionResult";
import { ApiResult } from "./Modules.System.Models.ApiResult";
import { AuthResponse } from "./Modules.System.Models.AuthResponse";
import { ServiceOptions, serviceRequest } from "@serenity-is/corelib/q";

export namespace AccountApiService {
    export const baseUrl = '~/api/account';

    export declare function Login(request: LoginRequest, onSuccess?: (response: ActionResult<ApiResult<AuthResponse>>) => void, opt?: ServiceOptions<any>): JQueryXHR;

    export const Methods = {
        Login: "~/api/account/Login"
    } as const;

    [
        'Login'
    ].forEach(x => {
        (<any>AccountApiService)[x] = function (r, s, o) {
            return serviceRequest(baseUrl + '/' + x, r, s, o);
        };
    });
}