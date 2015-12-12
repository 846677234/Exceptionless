﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Models;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Repositories;
using Foundatio.Caching;
using Foundatio.Utility;

namespace Exceptionless.Core.Plugins.EventProcessor.Default {
    [Priority(60)]
    public class AutoSessionPlugin : EventProcessorPluginBase {
        private static readonly TimeSpan _sessionTimeout = TimeSpan.FromDays(1);
        private readonly ICacheClient _cacheClient;
        private readonly IEventRepository _eventRepository;
        private readonly UpdateStatsAction _updateStats;
        private readonly AssignToStackAction _assignToStack;

        public AutoSessionPlugin(ICacheClient cacheClient, IEventRepository eventRepository, AssignToStackAction assignToStack, UpdateStatsAction updateStats) {
            _cacheClient = new ScopedCacheClient(cacheClient, "session");
            _eventRepository = eventRepository;
            _assignToStack = assignToStack;
            _updateStats = updateStats;
        }

        public override async Task EventBatchProcessingAsync(ICollection<EventContext> contexts) {
            var identityGroups = contexts.Where(c => c.Event.GetUserIdentity()?.Identity != null)
                .OrderBy(c => c.Event.Date)
                .GroupBy(c => c.Event.GetUserIdentity().Identity);
            
            foreach (var identityGroup in identityGroups) {
                string sessionId = null;

                foreach (var context in identityGroup.Where(c => String.IsNullOrEmpty(c.Event.SessionId))) {
                    string cacheKey = $"{context.Project.Id}:identity:{identityGroup.Key}";

                    if (String.IsNullOrEmpty(sessionId) && !context.Event.IsSessionStart()) {
                        sessionId = await _cacheClient.GetAsync<string>(cacheKey, null).AnyContext();
                        await _cacheClient.SetExpirationAsync(cacheKey, _sessionTimeout).AnyContext();
                    }

                    if (context.Event.IsSessionStart() || String.IsNullOrEmpty(sessionId)) {
                        sessionId = ObjectId.GenerateNewId(context.Event.Date.DateTime).ToString();
                        await _cacheClient.SetAsync(cacheKey, sessionId, _sessionTimeout).AnyContext();
                        
                        if (!context.Event.IsSessionStart()) {
                            string sessionStartId = await CreateSessionStartEventAsync(context, sessionId).AnyContext();
                            await _cacheClient.SetAsync($"{context.Project.Id}:start:{sessionId}", sessionStartId).AnyContext();
                        }
                    }
                    
                    context.Event.SessionId = sessionId;

                    if (context.Event.IsSessionEnd()) {
                        sessionId = null;
                        await _cacheClient.RemoveAsync(cacheKey).AnyContext();
                    }
                }
            }
        }

        private async Task<string> CreateSessionStartEventAsync(EventContext context, string sessionId) {
            var startEvent = new PersistentEvent {
                SessionId = sessionId,
                Data = context.Event.Data,
                Date = context.Event.Date,
                Geo = context.Event.Geo,
                OrganizationId = context.Event.OrganizationId,
                ProjectId = context.Event.ProjectId,
                Tags = context.Event.Tags,
                Type = Event.KnownTypes.SessionStart
            };

            startEvent.CopyDataToIndex();
            
            var startEventContexts = new List<EventContext> {
                new EventContext(startEvent) { Project = context.Project, Organization = context.Organization }
            };

            await _assignToStack.ProcessBatchAsync(startEventContexts).AnyContext();
            await _updateStats.ProcessBatchAsync(startEventContexts).AnyContext();
            await _eventRepository.AddAsync(startEvent).AnyContext();

            return startEvent.Id;
        }
    }
}