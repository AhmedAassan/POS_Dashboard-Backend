using System.ComponentModel;
using Serenity.ComponentModel;
using Serenity.Data;
using Serenity.Data.Mapping;

namespace PosDashboard.Administration
{
    [ConnectionKey("Default"), Module("Administration"), TableName("Languages")]
    [DisplayName("Languages"), InstanceName("Language"), GenerateFields]
    [ReadPermission(PermissionKeys.Translation)]
    [ModifyPermission(PermissionKeys.Translation)]
    [LookupScript(typeof(Lookups.LanguageLookup))]
    public sealed partial class LanguageRow : Row<LanguageRow.RowFields>
    {
        [DisplayName("Id"), Identity, IdProperty]
        public int? Id { get => fields.Id[this]; set => fields.Id[this] = value; }

        [DisplayName("Language Id"), Size(10), NotNull, QuickSearch]
        public string LanguageId { get => fields.LanguageId[this]; set => fields.LanguageId[this] = value; }

        [DisplayName("Language Name"), Size(50), NotNull, QuickSearch, NameProperty]
        public string LanguageName { get => fields.LanguageName[this]; set => fields.LanguageName[this] = value; }
    }
}