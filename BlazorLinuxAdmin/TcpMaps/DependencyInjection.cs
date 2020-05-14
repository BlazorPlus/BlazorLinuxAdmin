using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Microsoft.Extensions.DependencyInjection
{
    using BlazorLinuxAdmin.TcpMaps;

    public static class TcpMapsBuilder
    {
        public static IServiceCollection AddTcpMaps(this IServiceCollection services)
        {
            TcpMapService.AddTcpMaps(services);

            return services;
        }

    }
}
