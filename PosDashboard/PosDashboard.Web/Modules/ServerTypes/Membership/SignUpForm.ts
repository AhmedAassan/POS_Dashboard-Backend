import { StringEditor, EmailAddressEditor, PasswordEditor, Widget, PrefixedContext } from "@serenity-is/corelib";
import { initFormType } from "@serenity-is/corelib/q";

export interface SignUpForm {
    DisplayName: StringEditor;
    Email: EmailAddressEditor;
    ConfirmEmail: EmailAddressEditor;
    Password: PasswordEditor;
    ConfirmPassword: PasswordEditor;
    Recaptcha: Widget;
}

export class SignUpForm extends PrefixedContext {
    static formKey = 'Membership.SignUp';
    private static init: boolean;

    constructor(prefix: string) {
        super(prefix);

        if (!SignUpForm.init)  {
            SignUpForm.init = true;

            var w0 = StringEditor;
            var w1 = EmailAddressEditor;
            var w2 = PasswordEditor;
            var w3 = Widget;

            initFormType(SignUpForm, [
                'DisplayName', w0,
                'Email', w1,
                'ConfirmEmail', w1,
                'Password', w2,
                'ConfirmPassword', w2,
                'Recaptcha', w3
            ]);
        }
    }
}