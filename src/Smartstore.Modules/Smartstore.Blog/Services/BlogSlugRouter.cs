﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Smartstore.Core.Seo;
using Smartstore.Core.Seo.Routing;

namespace Smartstore.Blog.Services
{
    public class BlogSlugRouter : SlugRouter
    {
        const string EntityName = "blogpost";

        public override RouteValueDictionary GetRouteValues(UrlRecord urlRecord, RouteValueDictionary values)
        {
            if (urlRecord.EntityName.ToLowerInvariant() == EntityName)
            {
                return new RouteValueDictionary
                {
                    { "area", string.Empty },
                    { "controller", "Blog" },
                    { "action", "BlogPost" },
                    { "blogPostId", urlRecord.EntityId },
                    { "entity", urlRecord }
                };
            }

            return null;
        }

        public override void MapRoutes(IEndpointRouteBuilder routes)
        {
            routes.MapLocalizedControllerRoute("BlogPost", UrlPatternFor("BlogPost"), new { controller = "Blog", action = "BlogPost" });
        }
    }
}
