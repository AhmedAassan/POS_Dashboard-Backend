using FluentMigrator;

namespace PosDashboard.Migrations.DefaultDB
{
    [Migration(20170304_0852)]
    public class DefaultDB_20170304_0852_TwoFactorAuth : AutoReversingMigration
    {
        public override void Up()
        {
            Alter.Table("Users")
                .AddColumn("MobilePhoneNumber").AsString(20).Nullable()
                .AddColumn("MobilePhoneVerified").AsBoolean().NotNullable().WithDefaultValue(false)
                .AddColumn("TwoFactorAuth").AsInt32().Nullable();
        }
    }
}