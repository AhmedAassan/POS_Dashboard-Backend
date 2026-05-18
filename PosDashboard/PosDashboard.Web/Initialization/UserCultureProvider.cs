using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace PosDashboard.AppServices
{
    public class UserCultureProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult> DetermineProviderCultureResult(HttpContext httpContext)
        {
            var culture = httpContext.Request.Cookies["LanguagePreference"];
            if (string.IsNullOrEmpty(culture) ||
                culture.Length > 5)
                return NullProviderCultureResult;

            if (culture.Length == 2)
            {
                if (TwoLetterToFourLetter.TryGetValue(culture, out string code))
                    culture = code;
                else
                    culture = culture + "-" + culture.ToUpperInvariant();
            }

            return Task.FromResult(new ProviderCultureResult(culture));
        }

        private static readonly Dictionary<string, string> TwoLetterToFourLetter =
            new(StringComparer.OrdinalIgnoreCase)
        {
            { "ar", "ar-SA" },
            { "bn", "bn-BD" },
            { "cs", "cs-CZ" },
            { "da", "da-DK" },
            { "en", "en-US" },
            { "fa", "fa-IR" },
            { "ja", "ja-JP" },
            { "he", "he-IL" },
            { "hi", "hi-IN" },
            { "ko", "ko-KR" },
            { "uk", "uk-UA" },
            { "sv", "sv-SE" },
            { "vi", "vi-VN" },
            { "ur", "ur-PK" },
            { "zh", "zh-CN" },
        };

        private static List<CultureInfo> supportedCultures;
        private static readonly string[] supportedCultureIdentifiers = new string[] {
            "ar-SA",
            "ar-EG",
            "ar-IQ",
            "ar-SD",
            "ar-MA",
            "ar-DZ",
            "ar-QA",
            "ar-SY",
            "ar-TN",
            "ar-AE",
            "ar-LB",
            "bg-BG",
            "bn-BD",
            "cs-CZ",
            "da-DK",
            "de-DE",
            "de-AT",
            "de-CH",
            "de-BE",
            "el-GR",
            "en-US",
            "en-GB",
            "en-CA",
            "en-AU",
            "en-IN",
            "en-ZA",
            "en-NZ",
            "en-IE",
            "en-SG",
            "es-ES",
            "es-MX",
            "es-CO",
            "es-AR",
            "es-PE",
            "es-VE",
            "es-CL",
            "es-EC",
            "es-GT",
            "fa-IR",
            "fi-FI",
            "fr-FR",
            "fr-CA",
            "fr-CD",
            "he-IL",
            "hi-IN",
            "hr-HR",
            "hu-HU",
            "id-ID",
            "it-IT",
            "ja-JP",
            "ko-KR",
            "nb-NO",
            "nl-NL",
            "nl-BE",
            "nn-NO",
            "pl-PL",
            "pt-PT",
            "pt-BR",
            "pt-AO",
            "pt-MZ",
            "ro-RO",
            "ru-RU",
            "ru-BY",
            "ru-KZ",
            "ru-KG",
            "sk-SK",
            "sv-SE",
            "tr-TR",
            "uk-UA",
            "ur-PK",
            "vi-VN",
            "zh-CN",
            "zh-TW",
            "zh-HK"
        };

        public static IList<CultureInfo> SupportedCultures
        {
            get => supportedCultures ??= supportedCultureIdentifiers.Select(x =>
            {
                try
                {
                    return new CultureInfo(x);
                }
                catch
                {
                    return null;
                }
            }).Where(x => x != null).ToList();
        }
    }
}