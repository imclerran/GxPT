using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Usage accounting around cancelled streams. A cancelled request reports a stub
    // (ResponseUsage.Cancelled: id + provider only, zero counters meaning "unknown", null cost);
    // the billed truth arrives later from the generation record via ReconcileUsage. These pin the
    // two invariants that make that flow honest: the stub must not clobber the live context gauge
    // with zeros, and the reconcile must land the record's cost AND token counts for cancelled
    // requests (while leaving completed requests' stream-counted tokens alone).
    public class ConversationUsageTests
    {
        [Fact]
        public void Cancelled_stub_keeps_previous_context_gauge()
        {
            var convo = new Conversation(null);
            convo.RecordUsage(new ResponseUsage
            {
                Id = "gen-1",
                PromptTokens = 1200,
                CompletionTokens = 80,
                CachedTokens = 1000,
                CacheWriteTokens = 50,
                Cost = 0.01m
            });
            convo.RecordUsage(new ResponseUsage { Id = "gen-2", Cancelled = true });

            var s = convo.GetUsageStats();
            Assert.Equal(1200, s.LastPromptTokens); // not collapsed to the stub's zeros
            Assert.Equal(1000, s.LastCachedTokens);
            Assert.Equal(50, s.LastCacheWriteTokens);
            Assert.Equal(0.01m, s.TotalCost);       // stub's null cost added nothing
        }

        [Fact]
        public void Cancelled_reconcile_lands_billed_cost_tokens_and_gauge()
        {
            var convo = new Conversation(null);
            var stub = new ResponseUsage { Id = "gen-1", Cancelled = true };
            convo.RecordUsage(stub);

            bool changed = convo.ReconcileUsage(stub, new GenerationStats
            {
                TotalCost = 0.034m,
                PromptTokens = 2200,
                CompletionTokens = 310,
                ReasoningTokens = 100,
                CachedTokens = 2000,
                Cancelled = true
            });

            Assert.True(changed);
            var s = convo.GetUsageStats();
            Assert.Equal(0.034m, s.TotalCost);
            Assert.Equal(2200L, s.TotalPromptTokens);
            Assert.Equal(310L, s.TotalCompletionTokens);
            Assert.Equal(100L, s.TotalReasoningTokens);
            Assert.Equal(2000L, s.TotalCachedTokens);
            // the gauge now shows the cancelled request's real context size; the record carries
            // no cache-write count, so that resets to 0 rather than keeping a stale value
            Assert.Equal(2200, s.LastPromptTokens);
            Assert.Equal(2000, s.LastCachedTokens);
            Assert.Equal(0, s.LastCacheWriteTokens);
        }

        [Fact]
        public void Late_cancelled_reconcile_does_not_clobber_newer_gauge()
        {
            var convo = new Conversation(null);
            var stub = new ResponseUsage { Id = "gen-1", Cancelled = true };
            convo.RecordUsage(stub);
            // a newer request lands before the slow generation record does
            convo.RecordUsage(new ResponseUsage
            {
                Id = "gen-2",
                PromptTokens = 3000,
                CachedTokens = 2500,
                CacheWriteTokens = 100
            });

            convo.ReconcileUsage(stub, new GenerationStats { TotalCost = 0.01m, PromptTokens = 2200 });

            var s = convo.GetUsageStats();
            Assert.Equal(3000, s.LastPromptTokens);      // the newer request owns the gauge
            Assert.Equal(2500, s.LastCachedTokens);
            Assert.Equal(100, s.LastCacheWriteTokens);
            Assert.Equal(5200L, s.TotalPromptTokens);    // but the totals still gained the billed 2200
            Assert.Equal(0.01m, s.TotalCost);
        }

        [Fact]
        public void Completed_reconcile_corrects_cost_but_not_tokens()
        {
            var convo = new Conversation(null);
            var u = new ResponseUsage
            {
                Id = "gen-1",
                PromptTokens = 1000,
                CompletionTokens = 50,
                Cost = 0.02m
            };
            convo.RecordUsage(u);

            bool changed = convo.ReconcileUsage(u, new GenerationStats
            {
                TotalCost = 0.015m,
                CacheDiscount = 0.005m,
                PromptTokens = 990,   // tokenizer-normalization noise, not a correction target
                CompletionTokens = 48
            });

            Assert.True(changed);
            var s = convo.GetUsageStats();
            Assert.Equal(0.015m, s.TotalCost);
            Assert.Equal(0.005m, s.TotalCacheDiscount);
            Assert.Equal(1000L, s.TotalPromptTokens);    // stream counts stand for completed requests
            Assert.Equal(50L, s.TotalCompletionTokens);
            Assert.Equal(1000, s.LastPromptTokens);
        }
    }
}
