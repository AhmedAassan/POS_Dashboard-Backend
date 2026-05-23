using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Serialization;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Abstractions;
using Serenity.Data;
using Serenity.Extensions.DependencyInjection;
using Serenity.Localization;
using Serenity.Navigation;
using Serenity.Services;
using Serenity.Web;
using System;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Text.Json;
using static Serenity.CoreTexts;

namespace PosDashboard
{
    public partial class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment hostEnvironment)
        {
            Configuration = configuration;
            HostEnvironment = hostEnvironment;
            SqlSettings.AutoQuotedIdentifiers = true;
            RegisterDataProviders();
        }

        public IConfiguration Configuration { get; }
        public IWebHostEnvironment HostEnvironment { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ITypeSource>(new DefaultTypeSource(new[]
            {
                typeof(LocalTextRegistry).Assembly,
                typeof(ISqlConnections).Assembly,
                typeof(IRow).Assembly,
                typeof(SaveRequestHandler<>).Assembly,
                typeof(IDynamicScriptManager).Assembly,
                typeof(Startup).Assembly,
                typeof(Serenity.Extensions.EnvironmentSettings).Assembly,
                typeof(Serenity.Pro.Extensions.BackgroundJobManager).Assembly,
                typeof(Serenity.Pro.DataAuditLog.DataAuditLogPage).Assembly,
            }));

            services.ConfigureSection<ConnectionStringOptions>(Configuration);
            services.ConfigureSection<CssBundlingOptions>(Configuration);
            services.ConfigureSection<LocalTextPackages>(Configuration);
            services.ConfigureSection<ScriptBundlingOptions>(Configuration);
            services.ConfigureSection<UploadSettings>(Configuration);
            services.ConfigureSection<RecaptchaSettings>(Configuration);
            services.ConfigureSection<Serenity.Extensions.ClamAVSettings>(Configuration);
            services.ConfigureSection<Serenity.Extensions.EnvironmentSettings>(Configuration);
            services.ConfigureSection<Serenity.Extensions.SmtpSettings>(Configuration);
            services.ConfigureSection<Serenity.Pro.Extensions.BackgroundJobSettings>(Configuration);
            services.ConfigureSection<Serenity.Reporting.PuppeteerHtmlToPdfSettings>(Configuration);

            if (Configuration["UseForwardedHeaders"] == "True")
            {
                services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                });
            }

            services.Configure<RequestLocalizationOptions>(options =>
            {
                options.SupportedUICultures = AppServices.UserCultureProvider.SupportedCultures;
                options.SupportedCultures = AppServices.UserCultureProvider.SupportedCultures;
                options.RequestCultureProviders.Insert(Math.Max(options.RequestCultureProviders.Count - 1, 0),
                    new AppServices.UserCultureProvider()); // insert it before AcceptLanguage header provider
            });

            var dataProtectionKeysFolder = Configuration?["DataProtectionKeysFolder"];
            if (!string.IsNullOrEmpty(dataProtectionKeysFolder))
            {
                dataProtectionKeysFolder = Path.Combine(HostEnvironment.ContentRootPath, dataProtectionKeysFolder);
                if (Directory.Exists(dataProtectionKeysFolder))
                    services.AddDataProtection()
                        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysFolder));
            }

            services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-CSRF-TOKEN";
            });

            services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            services.Configure<IISServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            var builder = services.AddControllersWithViews(options =>
            {
                options.Filters.Add(typeof(AutoValidateAntiforgeryIgnoreBearerAttribute));
                options.Filters.Add(typeof(AntiforgeryCookieResultFilterAttribute));
                options.ModelBinderProviders.Insert(0, new ServiceEndpointModelBinderProvider());
                options.Conventions.Add(new ServiceEndpointActionModelConvention());
            })
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ContractResolver = new DefaultContractResolver();
                options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
            });

            Serenity.Pro.Extensions.ExceptionLog.Initialize(services, HostEnvironment.ApplicationName,
                Configuration["Data:Default:ConnectionString"], Configuration["Data:Default:ProviderName"], Configuration["Data:Default:Dialect"]);

            services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(o =>
            {
                o.Cookie.Name = ".AspNetAuth";
                o.LoginPath = new PathString("/Account/Login/");
                o.AccessDeniedPath = new PathString("/Account/AccessDenied");
                o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                o.SlidingExpiration = true;
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = Configuration["Jwt:Issuer"],
                    ValidAudience = Configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(Configuration["Jwt:Key"]!))
                };
            });

            services.AddAuthorization();

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConfiguration(Configuration.GetSection("Logging"));
                loggingBuilder.AddConsole();
                loggingBuilder.AddDebug();
            });

            services.AddCors(options =>
            {
                options.AddPolicy("DevCors", policy =>
                {
                    policy
                        .WithOrigins("http://localhost:4200", "https://shopics.net:290",
                                     "https://shopics.net:291", "https://shopics.net:292", 
                                     "https://shopics.net:293", "https://shopics.net:294",
                                     "https://shopics.net:295", "https://shopics.net:296",
                                     "https://shopics.net:289")
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                    // .AllowCredentials(); // استخدمها فقط لو هتستخدم Cookies cross-site
                });
            });

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IUserClaimCreator, DefaultUserClaimCreator>();
            services.AddSingleton<Serenity.Extensions.IEmailSender, Serenity.Extensions.EmailSender>();
            services.AddSingleton<IElevationHandler, Serenity.Extensions.DefaultElevationHandler>();
            services.AddSingleton<Serenity.Pro.Extensions.IBackgroundJobManager, Serenity.Pro.Extensions.BackgroundJobManager>();
            services.AddServiceHandlers();
            services.AddDynamicScripts();
            services.AddCssBundling();
            services.AddScriptBundling();
            services.AddSingleton<IUploadAVScanner, Serenity.Extensions.ClamAVUploadScanner>();
            services.AddUploadStorage();
            services.AddPuppeteerHtmlToPdf();
            services.AddReporting();
            services.AddSingleton<IUserPasswordValidator, Administration.UserPasswordValidator>();
            services.AddSingleton<IHttpContextItemsAccessor, HttpContextItemsAccessor>();
            services.AddSingleton<IUserAccessor, Administration.UserAccessor>();
            services.AddSingleton<IUserRetrieveService, Administration.UserRetrieveService>();
            services.AddSingleton<IPermissionService, Administration.PermissionService>();
            services.AddSingleton<Serenity.Net.Core.Authorization.IRolePermissionService, Administration.RolePermissionService>();
            services.AddSingleton<INavigationModelFactory, Common.NavigationModelFactory>();
            services.AddSingleton<Administration.ISMSService, Administration.FakeSMSService>();
            services.AddSingleton<IDataMigrations, DataMigrations>();

            //new
            services.AddScoped<JwtTokenService>();
            services.AddHttpClient();


            services.AddControllers()
            .AddJsonOptions(x =>
            x.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        }

        public static void InitializeLocalTexts(IServiceProvider services)
        {
            var env = services.GetRequiredService<IWebHostEnvironment>();
            services.AddBaseTexts()
                .AddJsonTexts(env.ContentRootFileProvider, "App_Data/texts");
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            RowFieldsProvider.SetDefaultFrom(app.ApplicationServices);

            var startNodeScripts = Configuration["StartNodeScripts"];
            if (!string.IsNullOrEmpty(startNodeScripts))
            {
                foreach (var script in startNodeScripts.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    app.StartNodeScript(script);
                }
            }

            InitializeLocalTexts(app.ApplicationServices);

            app.UseRequestLocalization();

            if (Configuration["UseForwardedHeaders"] == "True")
                app.UseForwardedHeaders();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            if (!string.IsNullOrEmpty(Configuration["UsePathBase"]))
                app.UsePathBase(Configuration["UsePathBase"]);

            app.UseHttpsRedirection();
            app.UseExceptionLogger();

            if (!env.IsDevelopment())
            {
                app.UseSourceMapSecurity(new()
                {
                    SkipPermission = Configuration["SourceMapSkipPermission"]
                });
            }

            app.UseStaticFiles();

            //app.UseStaticFiles(new StaticFileOptions
            //{
            //    FileProvider = new PhysicalFileProvider(
            //        Path.Combine(env.ContentRootPath, "wwwroot", "uploads", "payment-types")),
            //    RequestPath = "/uploads/payment-types"
            //});

            app.UseRouting();
            app.UseCors("DevCors");
            app.UseAuthentication();
            app.UseAuthorization();

            ConfigureTestPipeline?.Invoke(app);

            app.UseDynamicScripts();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();

                // SPA fallback: serve index.html for any non-API, non-file route
                endpoints.MapFallbackToFile("index.html");
            });

            var backgroundJobManager = app.ApplicationServices
                .GetRequiredService<Serenity.Pro.Extensions.IBackgroundJobManager>();
            backgroundJobManager.Initialize();

            app.ApplicationServices.GetRequiredService<IDataMigrations>().Initialize();

            
        }

        public static Action<IApplicationBuilder> ConfigureTestPipeline { get; set; }

        public static void RegisterDataProviders()
        {
            DbProviderFactories.RegisterFactory("System.Data.SqlClient", SqlClientFactory.Instance);
            DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient", SqlClientFactory.Instance);
            DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", Microsoft.Data.Sqlite.SqliteFactory.Instance);

            // to enable FIREBIRD: add FirebirdSql.Data.FirebirdClient reference, set connections, and uncomment line below
            // DbProviderFactories.RegisterFactory("FirebirdSql.Data.FirebirdClient", FirebirdSql.Data.FirebirdClient.FirebirdClientFactory.Instance);

            // to enable MYSQL: add MySql.Data reference, set connections, and uncomment line below
            // DbProviderFactories.RegisterFactory("MySql.Data.MySqlClient", MySql.Data.MySqlClient.MySqlClientFactory.Instance);

            // to enable POSTGRES: add Npgsql reference, set connections, and uncomment line below
            // DbProviderFactories.RegisterFactory("Npgsql", Npgsql.NpgsqlFactory.Instance);
        }
    }
}
