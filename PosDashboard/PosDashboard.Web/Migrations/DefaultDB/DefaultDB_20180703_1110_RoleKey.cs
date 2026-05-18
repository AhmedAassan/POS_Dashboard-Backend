using FluentMigrator;

namespace PosDashboard.Migrations.DefaultDB
{
    [Migration(20180703_1110)]
    public class DefaultDB_20180703_1110_RoleKey : Migration
    {
        public override void Up()
        {
            Alter.Table("Roles")
                .AddColumn("RoleKey").AsString(100).Nullable();
        }

        public override void Down()
        {
        }
    }
}