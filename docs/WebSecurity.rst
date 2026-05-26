ASP.NET Core Web Authentication
================================

Overview
^^^^^^^^^^

The ``Dotmim.Sync.Web.Server`` package wraps DMS into an ASP.NET Core Web API surface. From a security standpoint there is nothing special about it: securing the sync controller is exactly the same as securing any other Web API controller.

.. hint:: Sample: `Web Authentication sample <https://github.com/Mimetis/Dotmim.Sync/blob/master/Samples/HelloWebAuthSync>`_.

The base controller looks like:

.. code-block:: csharp

    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        private readonly WebServerAgent webServerAgent;
        private readonly IWebHostEnvironment env;

        public SyncController(WebServerAgent webServerAgent, IWebHostEnvironment env)
        {
            this.webServerAgent = webServerAgent;
            this.env = env;
        }

        [HttpPost]
        public Task Post() => webServerAgent.HandleRequestAsync(this.HttpContext);

        [HttpGet]
        public async Task Get()
        {
            if (env.IsDevelopment())
            {
                await this.HttpContext.WriteHelloAsync(webServerAgent);
            }
            else
            {
                await this.HttpContext.Response.WriteAsync("PRODUCTION MODE. HIDDEN INFO");
            }
        }
    }

To protect this API, plug in any standard ASP.NET Core authentication scheme. Common choices:

* OAuth2 / OpenID Connect via Microsoft Identity Web (Entra ID, B2C).
* JWT bearer validation against your own identity provider.
* AWS Cognito, Auth0, Okta, IdentityServer, etc.

A few external resources:

* `Mobile application calling a secure Web API (Microsoft) <https://docs.microsoft.com/en-us/azure/active-directory/develop/scenario-mobile-overview>`_
* `Securing an ASP.NET Core API with AWS Cognito <https://referbruv.com/blog/posts/securing-aspnet-core-apis-with-jwt-bearer-using-aws-cognito>`_
* `Identity Server: protecting an API <https://duendesoftware.com/products/identityserver>`_
* `ASP.NET Core authentication <https://docs.microsoft.com/en-us/aspnet/core/security/authentication>`_


Server side
^^^^^^^^^^^^^^

The example below uses **JWT bearer** validation. For production, plug in the JWT validation parameters of your real identity provider (Microsoft Identity Web, AWS Cognito, etc.) instead of hard-coding a key.

.. warning:: The hard-coded key snippet below is for illustration only. **Do not** ship a hard-coded signing key in production.

Configure ASP.NET Core authentication and DMS together:

.. code-block:: csharp

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddControllers();

    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(options => options.IdleTimeout = TimeSpan.FromMinutes(30));

    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear(); // keep raw claim types

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = "Dotmim.Sync.Bearer",
                ValidAudience = "Dotmim.Sync.Bearer",
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes("RANDOM_KEY_FROM_CONFIG_OR_KEYVAULT")),
            };
        });

    builder.Services.AddAuthorization();

    var connectionString = builder.Configuration.GetConnectionString("SqlConnection");

    var setup = new SyncSetup("ProductCategory", "ProductModel", "Product",
        "Address", "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail");

    builder.Services.AddSyncServer(new SqlSyncProvider(connectionString), setup);

    var app = builder.Build();
    app.UseHttpsRedirection();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseSession();
    app.MapControllers();
    app.Run();


Using **Microsoft Identity Web** (Entra ID / B2C):

.. code-block:: csharp

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddControllers();
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(options => options.IdleTimeout = TimeSpan.FromMinutes(30));

    builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration)
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddInMemoryTokenCaches();

    builder.Services.AddAuthorization();

    var connectionString = builder.Configuration.GetConnectionString("SqlConnection");
    var setup = new SyncSetup(/* ... */);
    builder.Services.AddSyncServer(new SqlSyncProvider(connectionString), setup);

    var app = builder.Build();
    app.UseHttpsRedirection();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseSession();
    app.MapControllers();
    app.Run();

.. note:: More on configuring a Microsoft Identity Web protected API: `Configuration <https://docs.microsoft.com/en-us/azure/active-directory/develop/scenario-protected-web-api-app-configuration>`_.


Securing the controller
-----------------------------

You can require authentication on the whole controller or per-method:

.. code-block:: csharp

    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        // ...
    }


Or mix ``[Authorize]`` with ``[AllowAnonymous]`` for the GET handler:

.. code-block:: csharp

    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        private readonly WebServerAgent webServerAgent;

        public SyncController(WebServerAgent webServerAgent)
            => this.webServerAgent = webServerAgent;

        [HttpPost]
        [Authorize]
        public Task Post() => webServerAgent.HandleRequestAsync(this.HttpContext);

        [HttpGet]
        [AllowAnonymous]
        public Task Get() => this.HttpContext.WriteHelloAsync(webServerAgent);
    }


Or check claims explicitly inside the action:

.. code-block:: csharp

    [HttpPost]
    public async Task Post()
    {
        if (!HttpContext.User.Identity.IsAuthenticated)
        {
            this.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var scope = User.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value;
        var user = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (scope != "access_as_user")
        {
            this.HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await webServerAgent.HandleRequestAsync(this.HttpContext);
    }


Client side
^^^^^^^^^^^^^^^

Pass an authenticated ``HttpClient`` to the ``WebRemoteOrchestrator``. The orchestrator accepts an ``HttpClient`` parameter; whatever default ``DefaultRequestHeaders`` you set will travel with every sync request.

.. code-block:: csharp

    // Get a JWT from your identity provider.
    var token = await GetTokenAsync(/* ... */);

    var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", token);

    var serverOrchestrator = new WebRemoteOrchestrator(
        "https://localhost:44342/api/sync",
        client: httpClient);

    var clientProvider = new SqlSyncProvider(clientConnectionString);
    var agent = new SyncAgent(clientProvider, serverOrchestrator);

    var result = await agent.SynchronizeAsync();


MSAL token acquisition (mobile / desktop)
--------------------------------------------

For native clients, MSAL takes care of acquiring and refreshing tokens silently:

.. code-block:: csharp

    string[] scopes = { "user.read" };
    var app = PublicClientApplicationBuilder.Create(clientId).Build();
    var accounts = await app.GetAccountsAsync();

    AuthenticationResult result;
    try
    {
        result = await app.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                          .ExecuteAsync();
    }
    catch (MsalUiRequiredException)
    {
        result = await app.AcquireTokenInteractive(scopes).ExecuteAsync();
    }

    httpClient.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", result.AccessToken);

.. note:: More on mobile token acquisition: `Acquire token from a mobile application <https://docs.microsoft.com/en-us/azure/active-directory/develop/scenario-mobile-acquire-token>`_.
