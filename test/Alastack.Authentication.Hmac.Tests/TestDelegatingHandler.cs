﻿using System.Net;
using System.Text;
using static System.Net.Mime.MediaTypeNames;

namespace Alastack.Authentication.Hmac.Tests
{
    public class TestDelegatingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"name\":\"value\"}", Encoding.UTF8, Application.Json)
            };
            return await Task.FromResult(response);
        }
    }
}