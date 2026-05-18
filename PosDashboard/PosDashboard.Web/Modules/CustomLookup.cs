using OfficeOpenXml.FormulaParsing.Excel.Functions.RefAndLookup;
using Serenity.ComponentModel;
using Serenity.Data;
using Serenity.Web;
using System;
using PosDashboard.Administration;

using static PosDashboard.Web.Modules.PermissionKeys;

namespace PosDashboard.Web.Modules
{
    public class CustomLookup
    {
        //[LookupScript("Archiving.ArchiveFolders", Permission = PermissionKeys.ArchivingPermissionKeys.ArchiveView)]
        //public sealed class ArchiveFolderLookup : RowLookupScript<ArchiveItemsRow>
        //{
        //    public ArchiveFolderLookup(ISqlConnections sqlConnections)
        //        : base(sqlConnections)
        //    {
        //        IdField = nameof(ArchiveItemsRow.ItemId);
        //        TextField = nameof(ArchiveItemsRow.ItemName);
        //        ParentIdField = nameof(ArchiveItemsRow.ParentId);
        //        Expiration = TimeSpan.FromMinutes(5);
        //    }

        //    protected override void PrepareQuery(SqlQuery query)
        //    {
        //        base.PrepareQuery(query);

        //        var fld = ArchiveItemsRow.Fields;

        //        query.Select(fld.ItemId)
        //             .Select(fld.ItemName)
        //             .Select(fld.ParentId)
        //             .Where(fld.IsFolder == 1)
        //             .Where(fld.IsDeleted == 0)
        //             .OrderBy(fld.ItemName);
        //    }

        //    protected override void ApplyOrder(SqlQuery query)
        //    {
        //        // Already ordered in PrepareQuery
        //    }
        //}


        //[LookupScript("Archiving.ActiveCategories", Permission = PermissionKeys.ArchivingPermissionKeys.ArchiveView)]
        //public sealed class ActiveCategoriesLookup : RowLookupScript<ArchiveCategoriesRow>
        //{
        //    public ActiveCategoriesLookup(ISqlConnections sqlConnections)
        //        : base(sqlConnections)
        //    {
        //        IdField = nameof(ArchiveCategoriesRow.CategoryId);
        //        TextField = nameof(ArchiveCategoriesRow.CategoryName);
        //        ParentIdField = nameof(ArchiveCategoriesRow.ParentCategoryId);
        //        Expiration = TimeSpan.FromMinutes(5);
        //    }

        //    protected override void PrepareQuery(SqlQuery query)
        //    {
        //        base.PrepareQuery(query);

        //        var fld = ArchiveCategoriesRow.Fields;

        //        query.Select(fld.CategoryId)
        //             .Select(fld.CategoryName)
        //             .Select(fld.ParentCategoryId)
        //             .Select(fld.SortOrder)
        //             .Where(fld.IsActive == 1)
        //             .OrderBy(fld.SortOrder)
        //             .OrderBy(fld.CategoryName);
        //    }

        //    protected override void ApplyOrder(SqlQuery query)
        //    {
        //        // Already ordered in PrepareQuery
        //    }
        //}



        [LookupScript(Permission = PermissionKeys.ArchivingPermissionKeys.View)] 
        public class UserContentLookup : RowLookupScript<UserRow>
        {
            public UserContentLookup(ISqlConnections connections)
                : base(connections)
            {
                IdField = "UserId";
                TextField = "Username";

                Expiration = TimeSpan.FromDays(-1);
            }

            protected override void PrepareQuery(SqlQuery query)
            {
                base.PrepareQuery(query);

                query
                    .Select("UserId")
                    .Select("Username");
            }
        }
    }
}
