import { AuthResponse } from "./Modules.System.Models.AuthResponse";

export interface ApiResult<AuthResponse> {
    Success?: boolean;
    Error?: string;
    Data?: AuthResponse;
}