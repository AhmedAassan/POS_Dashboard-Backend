/** @jsxImportSource jsx-dom */
import { LoginForm, LoginPageModel, LoginRequest } from "@/ServerTypes/Membership";
import { Texts } from "@/ServerTypes/Texts";
import { PropertyPanel } from "@serenity-is/corelib";
import { ErrorHandling, format, getCookie, notifyError, parseQueryString, resolveUrl, serviceCall, tryGetText } from "@serenity-is/corelib/q";
import { PromptDialog } from "@serenity-is/extensions";
import { AccountPanelTitle } from "../AccountPanelTitle";

export default function pageInit(opt?: { activated: string, isPublicDemo: boolean, model: LoginPageModel }) {
    var loginPanel = new LoginPanel($('#LoginPanel'), opt?.model);

    if (opt?.activated) {
        loginPanel.form.Username.value = opt.activated;
        loginPanel.form.Password.element.focus();
    }
    else if (opt?.isPublicDemo) {
        loginPanel.form.Username.element.val('admin').attr('placeholder', 'admin')
        loginPanel.form.Password.element.val('serenity').attr('placeholder', 'serenity');
    }
}

class LoginPanel extends PropertyPanel<LoginRequest, LoginPageModel> {

    public readonly form = new LoginForm(this.idPrefix);

    protected getFormKey() { return LoginForm.formKey; }

    constructor(element: JQuery, options?: LoginPageModel) {
        super(element, options);
    }

    protected loginClick() {
        if (!this.validateForm())
            return;

        var request = this.getSaveEntity();

        serviceCall({
            url: resolveUrl('~/Account/Login'),
            request: request,
            onSuccess: () => {
                this.redirectToReturnUrl();
            },
            onError: response => {
                if (response?.Error?.Code === "TwoFactorAuthenticationRequired") {
                    var args = response.Error.Arguments.split('|');
                    this.handleTwoFactorAuthentication(request.Username, request.Password, args[1], args[0]);
                    return;
                }

                if (response?.Error?.Code === "RedirectUserTo") {
                    window.location.href = response.Error.Arguments;
                    return;
                }

                if (response?.Error?.Message?.length) {
                    notifyError(response.Error.Message);
                    this.form.Password.element.focus();

                    return;
                }

                ErrorHandling.showServiceError(response.Error);
            }
        });

    }

    protected getReturnUrl() {
        var q = parseQueryString();
        return q['returnUrl'] || q['ReturnUrl'];
    }

    protected redirectToReturnUrl() {
        var returnUrl = this.getReturnUrl();
        if (returnUrl && /^\//.test(returnUrl)) {
            var hash = window.location.hash;
            if (hash != null && hash != '#')
                returnUrl += hash;
            window.location.href = returnUrl;
        }
        else {
            window.location.href = resolveUrl('~/');
        }
    }

    protected handleTwoFactorAuthentication(user: string, pass: string, twoFactorGuid: string, info: string) {
        var tries = 0;
        var remaining = 120;

        var dialog = null;

        var showDialog = () => {
            dialog = new PromptDialog({
                title: "Two Factor Authentication",
                editorOptions: {
                    maxLength: 4,
                },
                editorType: "Integer",
                message: info,
                isHtml: true,
                required: true,
                validateValue: (x) => {
                    if (x >= 1000 && x <= 9999) {
                        tries++;

                        serviceCall({
                            url: resolveUrl('~/Account/Login'),
                            request: {
                                Username: user,
                                Password: pass,
                                TwoFactorGuid: twoFactorGuid,
                                TwoFactorCode: x
                            },
                            onSuccess: () => {
                                this.redirectToReturnUrl();
                                return;
                            },
                            onError: z => {
                                notifyError(z.Error.Message);

                                if (tries > 2) {
                                    notifyError("Code entered is invalid! You can't try more than 3 times!");
                                    dialog = null;
                                    return;
                                }

                                showDialog();
                            }
                        });

                        return true;
                    }

                    notifyError("Please enter a valid code!");
                    return false;
                }
            });

            dialog.dialogOpen();
            dialog.element.on("dialogclose.me", function () {
                if (dialog != null) {
                    dialog.element.off("dialogclose.me");
                    dialog = null;
                }
            });
        };

        function updateCounter() {
            remaining -= 1;
            if (dialog != null) {
                dialog.element.find("span.counter").text(remaining.toString());
            }

            if (remaining >= 0)
                setTimeout(updateCounter, 1000);
            else if (dialog != null)
                dialog.dialogClose();
        };

        showDialog();
        window.setTimeout(updateCounter, 1000);
    }

    protected renderContents() {
        const id = this.useIdPrefix();
        const myTexts = Texts.Forms.Membership.Login;
        const returnUrl = this.getReturnUrl();
        this.element.empty().append(<>
            <AccountPanelTitle />
            <div class="s-Panel p-4">
                <h5 class="text-center my-4">{myTexts.LoginToYourAccount}</h5>
                <form id={id.Form} action="">
                    <div id={id.PropertyGrid}></div>
                    <div class="px-field">
                        <a class="float-end text-decoration-none" href={resolveUrl('~/Account/ForgotPassword')}>
                            {myTexts.ForgotPassword}
                        </a>
                    </div>
                    <div class="px-field">
                        <button id={id.LoginButton} type="submit" class="btn btn-primary my-3 w-100"
                            onClick={e => {
                                e.preventDefault();
                                this.loginClick();
                            }}>
                            {myTexts.SignInButton}
                        </button>
                    </div>
                </form>
            </div>
            <div class="text-center mt-2">
                <a class="text-decoration-none" href={resolveUrl('~/Account/SignUp')}>{myTexts.SignUpButton}</a>
            </div>
        </>);
    }
}