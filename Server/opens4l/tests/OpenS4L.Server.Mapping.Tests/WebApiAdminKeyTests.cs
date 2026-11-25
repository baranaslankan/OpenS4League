using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using OpenS4L.Plugins.WebApi;
using OpenS4L.Plugins.WebApi.Mappers;
using OpenS4L.Server.Game.Services;
using Serilog;
using Xunit;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// With an AdminKey set, /admin/* needs a matching X-Admin-Key header; the read-only routes
    /// stay open. The missing-command 400 proves a request with the right key reaches its route.
    /// </summary>
    public class WebApiAdminKeyTests : IDisposable
    {
        private const string Key = "test-admin-key";
        private readonly HttpClient _client;
        private readonly GameDataService _gds = GameFixtures.CreateGameDataService();

        public WebApiAdminKeyTests()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            var app = builder.Build();
            Endpoints.Map(app, new GdsProvider(_gds), Log.Logger, new WebApiMapper(_gds), Key);
            app.StartAsync().GetAwaiter().GetResult();
            _client = app.GetTestClient();
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private Task<HttpResponseMessage> PostCommand(string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/admin/command")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>())
            };
            if (key != null)
                request.Headers.Add(Endpoints.AdminKeyHeader, key);
            return _client.SendAsync(request);
        }

        [Fact]
        public async Task Admin_without_key_returns401()
        {
            var response = await PostCommand(null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Admin_with_wrong_key_returns401()
        {
            var response = await PostCommand("wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Admin_with_key_reaches_route()
        {
            var response = await PostCommand(Key);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Read_routes_need_no_key()
        {
            var response = await _client.GetAsync("/gamedata/maps");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private sealed class GdsProvider : IServiceProvider
        {
            private readonly GameDataService _gds;

            public GdsProvider(GameDataService gds) => _gds = gds;

            public object GetService(Type serviceType) =>
                serviceType == typeof(GameDataService) ? _gds : null;
        }
    }
}
