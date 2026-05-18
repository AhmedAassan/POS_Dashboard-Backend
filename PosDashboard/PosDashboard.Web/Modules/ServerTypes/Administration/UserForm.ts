import { StringEditor, EmailAddressEditor, LookupEditor, BooleanEditor, EnumEditor, ImageUploadEditor, PasswordEditor, PrefixedContext } from "@serenity-is/corelib";
import { TwoFactorAuthType } from "./TwoFactorAuthType";
import { initFormType } from "@serenity-is/corelib/q";

export interface UserForm {
    Username: StringEditor;
    DisplayName: StringEditor;
    Email: EmailAddressEditor;
    Roles: LookupEditor;
    MobilePhoneNumber: StringEditor;
    MobilePhoneVerified: BooleanEditor;
    TwoFactorAuth: EnumEditor;
    UserImage: ImageUploadEditor;
    Password: PasswordEditor;
    PasswordConfirm: PasswordEditor;
    Source: StringEditor;
    IsActive: BooleanEditor;
}

export class UserForm extends PrefixedContext {
    static formKey = 'Administration.User';
    private static init: boolean;

    constructor(prefix: string) {
        super(prefix);

        if (!UserForm.init)  {
            UserForm.init = true;

            var w0 = StringEditor;
            var w1 = EmailAddressEditor;
            var w2 = LookupEditor;
            var w3 = BooleanEditor;
            var w4 = EnumEditor;
            var w5 = ImageUploadEditor;
            var w6 = PasswordEditor;

            initFormType(UserForm, [
                'Username', w0,
                'DisplayName', w0,
                'Email', w1,
                'Roles', w2,
                'MobilePhoneNumber', w0,
                'MobilePhoneVerified', w3,
                'TwoFactorAuth', w4,
                'UserImage', w5,
                'Password', w6,
                'PasswordConfirm', w6,
                'Source', w0,
                'IsActive', w3
            ]);
        }
    }
}

[TwoFactorAuthType]; // referenced types