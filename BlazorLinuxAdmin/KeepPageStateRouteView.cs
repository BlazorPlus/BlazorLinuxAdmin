using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Components.Rendering;
using System.Reflection;

namespace Microsoft.AspNetCore.Components   //use this namepace so copy/paste this code easier
{

    public class KeepPageStateRouteView : RouteView
    {
        protected override void Render(RenderTreeBuilder builder)
        {
            var layoutType = RouteData.PageType.GetCustomAttribute<LayoutAttribute>()?.LayoutType ?? DefaultLayout;
            builder.OpenComponent<LayoutView>(0);
            builder.AddAttribute(1, "Layout", layoutType);
            builder.AddAttribute(2, "ChildContent", (RenderFragment)CreateBody());
            builder.CloseComponent();
        }

        RenderFragment CreateBody()
        {
            var pagetype = RouteData.PageType;
            var routeValues = RouteData.RouteValues;

            void RenderForLastValue(RenderTreeBuilder builder)
            {
                //dont reference RouteData again

                builder.OpenComponent(0, pagetype);
                foreach (KeyValuePair<string, object> routeValue in routeValues)
                {
                    builder.AddAttribute(1, routeValue.Key, routeValue.Value);
                }
                builder.CloseComponent();
            }

            return RenderForLastValue;
        }

    }

}
