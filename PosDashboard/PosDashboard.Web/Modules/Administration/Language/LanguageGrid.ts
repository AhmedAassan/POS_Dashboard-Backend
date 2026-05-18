import { LanguageColumns, LanguageRow, LanguageService } from "@/ServerTypes/Administration";
import { Decorators, EntityGrid } from "@serenity-is/corelib";
import { LanguageDialog } from "./LanguageDialog";

@Decorators.registerClass('PosDashboard.Administration.LanguageGrid')
export class LanguageGrid extends EntityGrid<LanguageRow, any> {
    protected useAsync() { return true; }
    protected getColumnsKey() { return LanguageColumns.columnsKey; }
    protected getDialogType() { return LanguageDialog; }
    protected getRowDefinition() { return LanguageRow; }
    protected getService() { return LanguageService.baseUrl; }

    protected getDefaultSortBy() {
        return [LanguageRow.Fields.LanguageName];
    }
}