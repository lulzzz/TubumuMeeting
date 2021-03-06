using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Tubumu.Core.Extensions;
using Tubumu.Core.Json;
using TubumuMeeting.Mediasoup;
using TubumuMeeting.Meeting.Server;
using TubumuMeeting.Meeting.Server.Authorization;

namespace TubumuMeeting.Web
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc()
                    .AddNewtonsoftJson(options =>
                    {
                        var settings = options.SerializerSettings;
                        settings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                        settings.DateFormatString = "yyyy'-'MM'-'dd' 'HH':'mm':'ss"; // 自定义日期格式。默认是 ISO8601 格式。
                        settings.Converters = new JsonConverter[] { new EnumStringValueEnumConverter() };
                    });

            // Cache
            services.AddDistributedRedisCache(options =>
            {
                options.Configuration = "localhost";
                options.InstanceName = "Meeting:";
            });
            services.AddMemoryCache();

            // Cors
            var corsSettings = Configuration.GetSection("CorsSettings").Get<CorsSettings>();
            services.AddCors(options => options.AddPolicy("DefaultPolicy",
                builder => builder.WithOrigins(corsSettings.Origins).AllowAnyMethod().AllowAnyHeader().AllowCredentials())
            //builder => builder.WithOrigins("*").AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader())
            );

            // Authentication
            services.AddSingleton<ITokenService, TokenService>();
            var tokenValidationSettings = Configuration.GetSection("TokenValidationSettings").Get<TokenValidationSettings>();
            services.AddSingleton(tokenValidationSettings);
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = tokenValidationSettings.ValidIssuer,
                        ValidateIssuer = true,

                        ValidAudience = tokenValidationSettings.ValidAudience,
                        ValidateAudience = true,

                        IssuerSigningKey = SignatureHelper.GenerateSigningKey(tokenValidationSettings.IssuerSigningKey),
                        ValidateIssuerSigningKey = true,

                        ValidateLifetime = tokenValidationSettings.ValidateLifetime,
                        ClockSkew = TimeSpan.FromSeconds(tokenValidationSettings.ClockSkewSeconds),
                    };

                    // We have to hook the OnMessageReceived event in order to
                    // allow the JWT authentication handler to read the access
                    // token from the query string when a WebSocket or 
                    // Server-Sent Events request comes in.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];

                            // If the request is for our hub...
                            var path = context.HttpContext.Request.Path;
                            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                            {
                                // Read the token out of the query string
                                context.Token = accessToken;
                            }
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            //_logger.LogError($"Authentication Failed(OnAuthenticationFailed): {context.Request.Path} Error: {context.Exception}");
                            if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
                            {
                                context.Response.Headers.Add("Token-Expired", "true");
                            }
                            return Task.CompletedTask;
                        },
                        OnChallenge = async context =>
                        {
                            //_logger.LogError($"Authentication Challenge(OnChallenge): {context.Request.Path}");
                            // TODO: (alby)为不同客户端返回不同的内容
                            var body = Encoding.UTF8.GetBytes("{\"code\": 400, \"message\": \"Authentication Challenge\"}");
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = "application/json";
                            await context.Response.Body.WriteAsync(body, 0, body.Length);
                            context.HandleResponse();
                        }
                    };
                });

            // SignalR
            services.AddSignalR(options =>
                {
                    options.EnableDetailedErrors = true;
                })
                .AddNewtonsoftJsonProtocol(options =>
                {
                    var settings = options.PayloadSerializerSettings;
                    settings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                    settings.DateFormatString = "yyyy'-'MM'-'dd' 'HH':'mm':'ss"; // 自定义日期格式。默认是 ISO8601 格式。
                    settings.Converters = new JsonConverter[] { new EnumStringValueEnumConverter() };
                });
            services.Replace(ServiceDescriptor.Singleton(typeof(IUserIdProvider), typeof(NameUserIdProvider)));

            var mediasoupStartupSettings = Configuration.GetSection("MediasoupStartupSettings").Get<MediasoupStartupSettings>();
            var workerSettings = Configuration.GetSection("MediasoupSettings:WorkerSettings").Get<WorkerSettings>();
            var routerSettings = Configuration.GetSection("MediasoupSettings:RouterSettings").Get<RouterSettings>();
            var webRtcTransportSettings = Configuration.GetSection("MediasoupSettings:WebRtcTransportSettings").Get<WebRtcTransportSettings>();
            services.AddMediasoup(options =>
            {
                // MediasoupStartupSettings
                if (mediasoupStartupSettings != null)
                {
                    options.MediasoupStartupSettings.MediasoupVersion = mediasoupStartupSettings.MediasoupVersion;
                    options.MediasoupStartupSettings.WorkerPath = mediasoupStartupSettings.WorkerPath;
                    options.MediasoupStartupSettings.NumberOfWorkers = !mediasoupStartupSettings.NumberOfWorkers.HasValue || mediasoupStartupSettings.NumberOfWorkers <= 0 ? Environment.ProcessorCount : mediasoupStartupSettings.NumberOfWorkers;
                }

                // WorkerSettings
                if (workerSettings != null)
                {
                    options.MediasoupSettings.WorkerSettings.LogLevel = workerSettings.LogLevel;
                    options.MediasoupSettings.WorkerSettings.LogTags = workerSettings.LogTags;
                    options.MediasoupSettings.WorkerSettings.RtcMinPort = workerSettings.RtcMinPort;
                    options.MediasoupSettings.WorkerSettings.RtcMaxPort = workerSettings.RtcMaxPort;
                    options.MediasoupSettings.WorkerSettings.DtlsCertificateFile = workerSettings.DtlsCertificateFile;
                    options.MediasoupSettings.WorkerSettings.DtlsPrivateKeyFile = workerSettings.DtlsPrivateKeyFile;
                }

                // RouteSettings
                if (routerSettings != null && !routerSettings.RtpCodecCapabilities.IsNullOrEmpty())
                {
                    options.MediasoupSettings.RouterSettings = routerSettings;
                }

                // WebRtcTransportSettings
                if (webRtcTransportSettings != null)
                {
                    options.MediasoupSettings.WebRtcTransportSettings.ListenIps = webRtcTransportSettings.ListenIps;
                    options.MediasoupSettings.WebRtcTransportSettings.InitialAvailableOutgoingBitrate = webRtcTransportSettings.InitialAvailableOutgoingBitrate;
                    options.MediasoupSettings.WebRtcTransportSettings.MinimumAvailableOutgoingBitrate = webRtcTransportSettings.MinimumAvailableOutgoingBitrate;
                    options.MediasoupSettings.WebRtcTransportSettings.MaxSctpMessageSize = webRtcTransportSettings.MaxSctpMessageSize;
                }
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHttpsRedirection();
            }

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseCors("DefaultPolicy");

            app.UseEndpoints(endpoints =>
            {
                // SignalR
                endpoints.MapHub<MeetingHub>("/hubs/meetingHub");
                endpoints.MapGet("/", async context =>
                {
                    await context.Response.WriteAsync("Tubumu Meeting");
                });
            });

            app.UseMediasoup();
        }
    }
}
