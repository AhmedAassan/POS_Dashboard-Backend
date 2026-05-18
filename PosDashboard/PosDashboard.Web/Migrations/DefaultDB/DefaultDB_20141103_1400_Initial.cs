using System;
using FluentMigrator;
using Serenity.Extensions;

namespace PosDashboard.Migrations.DefaultDB
{
    [Migration(20141103_1400)]
    public class DefaultDB_20141103_1400_Initial : AutoReversingMigration
    {
        public override void Up()
        {
            this.CreateTableWithId32("Users", "UserId", s => s
                .WithColumn("Username").AsString(100).NotNullable().Unique("IX_Users_Username")
                .WithColumn("DisplayName").AsString(100).NotNullable()
                .WithColumn("Email").AsString(100).Nullable()
                .WithColumn("Source").AsString(4).NotNullable()
                .WithColumn("PasswordHash").AsString(86).NotNullable()
                .WithColumn("PasswordSalt").AsString(10).NotNullable()
                .WithColumn("LastDirectoryUpdate").AsDateTime().Nullable()
                .WithColumn("UserImage").AsString(100).Nullable()
                .WithColumn("InsertDate").AsDateTime().NotNullable()
                .WithColumn("InsertUserId").AsInt32().NotNullable()
                .WithColumn("UpdateDate").AsDateTime().Nullable()
                .WithColumn("UpdateUserId").AsInt32().Nullable()
                .WithColumn("IsActive").AsInt16().NotNullable().WithDefaultValue(1));

            Insert.IntoTable("Users").Row(new
            {
                Username = "admin",
                DisplayName = "admin",
                Email = "admin@domain" + Serenity.IO.TemporaryFileHelper.RandomFileCode() + ".com",
                Source = "site",
                PasswordHash = "rfqpSPYs0ekFlPyvIRTXsdhE/qrTHFF+kKsAUla7pFkXL4BgLGlTe89GDX5DBysenMDj8AqbIZPybqvusyCjwQ",
                PasswordSalt = "hJf_F",
                InsertDate = new DateTime(2014, 1, 1),
                InsertUserId = 1,
                IsActive = 1
            });

            this.CreateTableWithId32("Languages", "Id", s => s
                .WithColumn("LanguageId").AsString(10).NotNullable().Unique("IX_Languages_LanguageId")
                .WithColumn("LanguageName").AsString(50).NotNullable());

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "en",
                LanguageName = "English"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "ar",
                LanguageName = "Arabic"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "bn",
                LanguageName = "Bangla"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "cs",
                LanguageName = "Czech"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "de",
                LanguageName = "German"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "es",
                LanguageName = "Spanish"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "fa",
                LanguageName = "Farsi"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "fr",
                LanguageName = "French"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "hi",
                LanguageName = "Hindi"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "id",
                LanguageName = "Indonesian"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "it",
                LanguageName = "Italian"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "ja",
                LanguageName = "Japanese"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "ko",
                LanguageName = "Korean"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "nl",
                LanguageName = "Dutch"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "pl",
                LanguageName = "Polish"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "pt",
                LanguageName = "Portuguese"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "pt-BR",
                LanguageName = "Portuguese (Brazil)"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "ro",
                LanguageName = "Romanian"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "ru",
                LanguageName = "Russian"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "sv",
                LanguageName = "Swedish"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "tr",
                LanguageName = "Turkish"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "vi-VN",
                LanguageName = "Vietnamese (Vietnam)"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "zh-CN",
                LanguageName = "Chinese (Simplified)"
            });

            Insert.IntoTable("Languages").Row(new
            {
                LanguageId = "zh-TW",
                LanguageName = "Chinese (Traditional)"
            });
        }
    }
}