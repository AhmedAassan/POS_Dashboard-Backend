using Serenity.Services;
using MyRequest = Serenity.Services.SaveRequest<PosDashboard.Administration.LanguageRow>;
using MyResponse = Serenity.Services.SaveResponse;
using MyRow = PosDashboard.Administration.LanguageRow;


namespace PosDashboard.Administration
{
    public interface ILanguageSaveHandler : ISaveHandler<MyRow, MyRequest, MyResponse> { }
    public class LanguageSaveHandler : SaveRequestHandler<MyRow, MyRequest, MyResponse>, ILanguageSaveHandler
    {
        public LanguageSaveHandler(IRequestContext context)
             : base(context)
        {
        }
    }
}