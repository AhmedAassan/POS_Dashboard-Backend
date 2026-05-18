import { LanguageRow } from "@/ServerTypes/Administration";

export async function siteLanguageList() {
    var result: string[][] = [];
    var languages = await LanguageRow.getLookupAsync();
    for (var k of languages.items) {
        if (k.LanguageId !== 'en')
            result.push([k.Id.toString(), k.LanguageName]);
    }
    return result;
}