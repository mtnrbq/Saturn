namespace Saturn

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.ResponseCompression
open Giraffe
open Microsoft.AspNetCore
open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open System.IO
open Microsoft.AspNetCore.Rewrite
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.IdentityModel.Tokens
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.OAuth
open System.Net.Http
open System.Net.Http.Headers
open Newtonsoft.Json.Linq
open System.Threading.Tasks

[<AutoOpen>]
module Application =
  type ApplicationState = {
    Router: HttpHandler option
    ErrorHandler: ErrorHandler option
    Pipelines: HttpHandler list
    Urls: string list
    AppConfigs: (IApplicationBuilder -> IApplicationBuilder) list
    HostConfigs: (IWebHostBuilder -> IWebHostBuilder) list
    ServicesConfig: (IServiceCollection -> IServiceCollection) list
  }

  type ApplicationBuilder internal () =
    member __.Yield(_) =
      let errorHandler (ex : Exception) (logger : ILogger) =
        logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
        clearResponse >=> Giraffe.HttpStatusCodeHandlers.ServerErrors.INTERNAL_ERROR ex.Message


      {Router = None; ErrorHandler = Some errorHandler; Pipelines = []; Urls = []; AppConfigs = []; HostConfigs = []; ServicesConfig = [] }

    member __.Run(state: ApplicationState) : IWebHost =
      match state.Router with
      | None -> failwith "Router needs to be defined in Saturn application"
      | Some router ->
      let router = (succeed |> List.foldBack (>=>) state.Pipelines) >=> router

      let appConfigs (app: IApplicationBuilder) =
        let app = app |> List.foldBack(fun e acc -> e acc) state.AppConfigs
        let app =
          match state.ErrorHandler with
          | Some err -> app.UseGiraffeErrorHandler(err)
          | None -> app
        app.UseGiraffe router

      let serviceConfigs (services : IServiceCollection) =
        let services = services.AddGiraffe()
        state.ServicesConfig |> List.rev |> List.iter (fun fn -> fn services |> ignore)

      let wbhst = WebHost.CreateDefaultBuilder() |> List.foldBack (fun e acc -> e acc ) state.HostConfigs
      wbhst
        .Configure(Action<IApplicationBuilder> appConfigs)
        .ConfigureServices(Action<IServiceCollection> serviceConfigs)
        .UseUrls(state.Urls |> List.toArray)
        .Build()

    ///Defines top-level router used for the application
    [<CustomOperation("router")>]
    member __.Router(state, handler) =
      {state with Router = Some handler}

    ///Adds pipeline to the list of pipelines that will be used for every request
    [<CustomOperation("pipe_through")>]
    member __.PipeThrough(state : ApplicationState, pipe) =
      {state with Pipelines = pipe::state.Pipelines}

    ///Adds error/not-found handler for current scope
    [<CustomOperation("error_handler")>]
    member __.ErrprHandler(state : ApplicationState, handler) =
      {state with ErrorHandler = Some handler}

    ///Adds custom application configuration step.
    [<CustomOperation("app_config")>]
    member __.AppConfig(state, config) =
      {state with AppConfigs = config::state.AppConfigs}

    ///Adds custom host configuration step.
    [<CustomOperation("host_config")>]
    member __.HostConfig(state, config) =
      {state with HostConfigs = config::state.HostConfigs}

    ///Adds custom service configuration step.
    [<CustomOperation("service_config")>]
    member __.ServiceConfig(state, config) =
      {state with ServicesConfig = config::state.ServicesConfig}

    ///Adds url
    [<CustomOperation("url")>]
    member __.Url(state, url) =
      {state with Urls = url::state.Urls}

    ///Adds logging configuration.
    [<CustomOperation("logging")>]
    member __.Logging(state, (config : ILoggingBuilder -> unit)) =
      {state with HostConfigs = (fun (app : IWebHostBuilder)-> app.ConfigureLogging(config))::state.HostConfigs}

    ///Enables in-memory session cache
    [<CustomOperation("memory_cache")>]
    member __.MemoryCache(state) =
      let service (s : IServiceCollection) = s.AddDistributedMemoryCache()
      let serviceSet (s : IServiceCollection) = s.AddSession()

      { state with
          ServicesConfig = serviceSet::(service::state.ServicesConfig)
          AppConfigs = (fun (app : IApplicationBuilder)-> app.UseSession())::state.AppConfigs
      }

    ///Enables gzip compression
    [<CustomOperation("use_gzip")>]
    member __.UseGZip(state : ApplicationState) =
      let service (s : IServiceCollection) =
        s.Configure<GzipCompressionProviderOptions>(fun (opts : GzipCompressionProviderOptions) -> opts.Level <- System.IO.Compression.CompressionLevel.Optimal)
         .AddResponseCompression()
      let middleware (app : IApplicationBuilder) = app.UseResponseCompression()

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }
    ///Enables using static file hosting.
    [<CustomOperation("use_static")>]
    member __.UseStatic(state, path : string) =
      let middleware (app : IApplicationBuilder) = app.UseStaticFiles()
      let host (builder: IWebHostBuilder) =
        let p = Path.Combine(Directory.GetCurrentDirectory(), path)
        builder
          .UseWebRoot(p)
          .UseContentRoot(p)
      { state with
          AppConfigs = middleware::state.AppConfigs
          HostConfigs = host::state.HostConfigs
      }

    [<CustomOperation("use_config")>]
    member __.UseConfig(state : ApplicationState, configBuilder : unit -> 'a) =
      let x = lazy(configBuilder ())
      let handler (nxt : HttpFunc) (ctx : Microsoft.AspNetCore.Http.HttpContext) : HttpFuncResult =
        ctx.Items.["Configuration"] <- x.Value
        nxt ctx

      {state with Pipelines = handler::state.Pipelines}

    ///Redirect all HTTP request to HTTPS
    [<CustomOperation("force_ssl")>]
    member __.ForceSSL(state : ApplicationState) =
      let middleware (app : IApplicationBuilder) =
        let opts = RewriteOptions().AddRedirectToHttps()
        app.UseRewriter opts

      {state with AppConfigs=middleware::state.AppConfigs}

    ///Enables application level CORS protection
    [<CustomOperation("use_cors")>]
    member __.UseCors(state: ApplicationState, policy : string, (policyConfig : CorsPolicyBuilder -> unit ) ) =
      let service (s : IServiceCollection) =
        s.AddCors(fun o -> o.AddPolicy(policy, policyConfig) |> ignore)
      let middleware (app : IApplicationBuilder) =
        app.UseCors policy

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables default JWT authentication
    [<CustomOperation("use_jwt_authentication")>]
    member __.UseJWTAuth(state: ApplicationState, secret: string, issuer : string) =
      let middleware (app : IApplicationBuilder) =
        app.UseAuthentication()

      let service (s : IServiceCollection) =
        s.AddAuthentication(fun cfg ->
          cfg.DefaultScheme <- JwtBearerDefaults.AuthenticationScheme
          cfg.DefaultChallengeScheme <- JwtBearerDefaults.AuthenticationScheme)
         .AddJwtBearer(fun opt ->
            let tvp = TokenValidationParameters()
            tvp.ValidateActor <- true
            tvp.ValidateAudience <- true
            tvp.ValidateLifetime <- true
            tvp.ValidateIssuerSigningKey <- true
            tvp.ValidIssuer <- issuer
            tvp.ValidAudience <- issuer
            tvp.IssuerSigningKey <- SymmetricSecurityKey(Text.Encoding.UTF8.GetBytes secret)
            opt.TokenValidationParameters <- tvp
         ) |> ignore
        s

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables JWT authentication with custom configuration
    [<CustomOperation("use_jwt_authentication_with_config")>]
    member __.UseJWTAuthConfig(state: ApplicationState, (config : JwtBearerOptions -> unit)) =
      let middleware (app : IApplicationBuilder) =
        app.UseAuthentication()

      let service (s : IServiceCollection) =
        s.AddAuthentication(fun cfg ->
          cfg.DefaultScheme <- JwtBearerDefaults.AuthenticationScheme
          cfg.DefaultChallengeScheme <- JwtBearerDefaults.AuthenticationScheme)
         .AddJwtBearer(Action<JwtBearerOptions> config) |> ignore
        s

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables default cookies authentication
    [<CustomOperation("use_cookies_authentication")>]
    member __.UseCookiesAuth(state: ApplicationState, issuer : string) =
      let middleware (app : IApplicationBuilder) =
        app.UseAuthentication()

      let service (s : IServiceCollection) =
        s.AddAuthentication(fun cfg ->
          cfg.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultChallengeScheme <- CookieAuthenticationDefaults.AuthenticationScheme)
         .AddCookie(fun opt ->
          opt.ClaimsIssuer <- issuer
         ) |> ignore
        s

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables cookies authentication with custom configuration
    [<CustomOperation("use_cookies_authentication_with_config")>]
    member __.UseCookiesAuthConfig(state: ApplicationState, (options :  CookieAuthenticationOptions -> unit) ) =
      let middleware (app : IApplicationBuilder) =
        app.UseAuthentication()

      let service (s : IServiceCollection) =
        s.AddAuthentication(fun cfg ->
          cfg.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultChallengeScheme <- CookieAuthenticationDefaults.AuthenticationScheme)
         .AddCookie(options) |> ignore
        s

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables default GitHub OAuth authentication
    [<CustomOperation("use_github_oauth")>]
    member __.UseGithubAuth(state: ApplicationState, clientId : string, clientSecret : string, callbackPath : string, jsonToClaimMap : (string * string) seq) =
      let middleware (app : IApplicationBuilder) =
        app.UseAuthentication()

      let service (s : IServiceCollection) =
        s.AddAuthentication(fun cfg ->
          cfg.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultSignInScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultChallengeScheme <- "GitHub")
         .AddCookie()
         .AddOAuth("GitHub", fun (opt : Authentication.OAuth.OAuthOptions) ->
          opt.ClientId <- clientId
          opt.ClientSecret <- clientSecret
          opt.CallbackPath <- PathString(callbackPath)
          opt.AuthorizationEndpoint <-  "https://github.com/login/oauth/authorize"
          opt.TokenEndpoint <- "https://github.com/login/oauth/access_token"
          opt.UserInformationEndpoint <- "https://api.github.com/user"
          jsonToClaimMap |> Seq.iter (fun (k,v) -> opt.ClaimActions.MapJsonKey(k,v) )
          let ev = OAuthEvents()

          ev.OnCreatingTicket <-
            fun ctx ->
              let tsk = task {
                let req = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint)
                req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
                req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", ctx.AccessToken)
                let! (response : HttpResponseMessage) = ctx.Backchannel.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.HttpContext.RequestAborted)
                response.EnsureSuccessStatusCode () |> ignore
                let! cnt = response.Content.ReadAsStringAsync()
                let user = JObject.Parse cnt
                ctx.RunClaimActions user
              }
              Task.Factory.StartNew(fun () -> tsk.Result)

         ) |> ignore
        s

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables GitHub OAuth authentication with custom configuration
    [<CustomOperation("use_github_oauth_with_config")>]
    member __.UseGithubAuthWithConfig(state: ApplicationState, (config : Authentication.OAuth.OAuthOptions -> unit) ) =
      let middleware (app : IApplicationBuilder) =
        app.UseAuthentication()

      let service (s : IServiceCollection) =
        s.AddAuthentication(fun cfg ->
          cfg.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultSignInScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultChallengeScheme <- "GitHub")
         .AddCookie()
         .AddOAuth("GitHub",config) |> ignore
        s

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables custom OAuth authentication
    [<CustomOperation("use_custom_oauth")>]
    member __.UseCustomOAuth(state: ApplicationState, name : string, (config : Authentication.OAuth.OAuthOptions -> unit) ) =
      let middleware (app : IApplicationBuilder) =
        app.UseAuthentication()

      let service (s : IServiceCollection) =
        s.AddAuthentication(fun cfg ->
          cfg.DefaultScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultSignInScheme <- CookieAuthenticationDefaults.AuthenticationScheme
          cfg.DefaultChallengeScheme <- name)
         .AddCookie()
         .AddOAuth(name,config) |> ignore
        s

      { state with
          ServicesConfig = service::state.ServicesConfig
          AppConfigs = middleware::state.AppConfigs
      }

    ///Enables IIS integration
    [<CustomOperation("use_iis")>]
    member __.UseIIS(state) =
      let host (builder: IWebHostBuilder) =
        builder.UseIISIntegration()
      { state with
          HostConfigs = host::state.HostConfigs
      }

  ///Computation expression used to configure Saturn application
  let application = ApplicationBuilder()

  ///Runs Saturn application
  let run (app: IWebHost) = app.Run()