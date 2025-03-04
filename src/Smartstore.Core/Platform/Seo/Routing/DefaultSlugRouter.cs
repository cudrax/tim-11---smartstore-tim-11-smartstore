﻿using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Smartstore.Core.Seo.Routing
{
    public class DefaultSlugRouter : SlugRouter
    {
        public override RouteValueDictionary GetRouteValues(UrlRecord entity, RouteValueDictionary values)
        {
            switch (entity.EntityName.ToLowerInvariant())
            {
                case "product":
                    return new RouteValueDictionary
                    {
                        { "area", string.Empty },
                        { "controller", "Product" },
                        { "action", "ProductDetails" },
                        { "productId", entity.EntityId },
                        { "entity", entity }
                    };
                case "category":
                    return new RouteValueDictionary
                    {
                        { "area", string.Empty },
                        { "controller", "Catalog" },
                        { "action", "Category" },
                        { "categoryId", entity.EntityId },
                        { "entity", entity }
                    };
                case "manufacturer":
                    return new RouteValueDictionary
                    {
                        { "area", string.Empty },
                        { "controller", "Catalog" },
                        { "action", "Manufacturer" },
                        { "manufacturerId", entity.EntityId },
                        { "entity", entity }
                    };
                case "topic":
                    return new RouteValueDictionary
                    {
                        { "area", string.Empty },
                        { "controller", "Topic" },
                        { "action", "TopicDetails" },
                        { "topicId", entity.EntityId },
                        { "entity", entity }
                    };
            }

            return null;
        }

        public override void MapRoutes(IEndpointRouteBuilder routes)
        {
            // TODO: (core) check all these SEO routes for correctness once all slug supporting entities are ported.
            routes.MapLocalizedControllerRoute("Product", UrlPatternFor("Product"), new { controller = "Product", action = "ProductDetails" });
            routes.MapLocalizedControllerRoute("Category", UrlPatternFor("Category"), new { controller = "Catalog", action = "Category" });
            routes.MapLocalizedControllerRoute("Manufacturer", UrlPatternFor("Manufacturer"), new { controller = "Catalog", action = "Manufacturer" });
            routes.MapLocalizedControllerRoute("Topic", UrlPatternFor("Topic"), new { controller = "Topic", action = "TopicDetails" });
        }
    }
}