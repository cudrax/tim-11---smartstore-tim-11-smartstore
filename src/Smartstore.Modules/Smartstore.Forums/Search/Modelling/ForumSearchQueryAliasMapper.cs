﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Smartstore.Caching;
using Smartstore.Core.Configuration;
using Smartstore.Core.Localization;
using Smartstore.Core.Search.Facets;

namespace Smartstore.Forums.Search.Modelling
{
    public partial class ForumSearchQueryAliasMapper : IForumSearchQueryAliasMapper
    {
        private const string ALL_FORUM_COMMONFACET_ALIAS_BY_KIND_KEY = "search.forum.commonfacet.alias.kind.mappings.all";

        private readonly ICacheManager _cache;
        private readonly ISettingService _settingService;
        private readonly ILanguageService _languageService;

        public ForumSearchQueryAliasMapper(
            ICacheManager cache, 
            ISettingService settingService,
            ILanguageService languageService)
        {
            _cache = cache;
            _settingService = settingService;
            _languageService = languageService;
        }

        public string GetCommonFacetAliasByGroupKind(FacetGroupKind kind, int languageId)
        {
            var mappings = GetCommonFacetAliasByGroupKindMappings();

            return mappings.Get(FacetUtility.GetFacetAliasSettingKey(kind, languageId, "Forum"));
        }

        public Task ClearCommonFacetCacheAsync()
        {
            return _cache.RemoveAsync(ALL_FORUM_COMMONFACET_ALIAS_BY_KIND_KEY);
        }

        protected virtual IDictionary<string, string> GetCommonFacetAliasByGroupKindMappings()
        {
            return _cache.Get(ALL_FORUM_COMMONFACET_ALIAS_BY_KIND_KEY, () =>
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var groupKinds = new[]
                {
                    FacetGroupKind.Forum,
                    FacetGroupKind.Customer,
                    FacetGroupKind.Date
                };

                foreach (var language in _languageService.GetAllLanguages())
                {
                    foreach (var groupKind in groupKinds)
                    {
                        var key = FacetUtility.GetFacetAliasSettingKey(groupKind, language.Id, "Forum");
                        var value = _settingService.GetSettingByKey<string>(key);
                        if (value.HasValue())
                        {
                            result.Add(key, value);
                        }
                    }
                }

                return result;
            });
        }
    }
}
