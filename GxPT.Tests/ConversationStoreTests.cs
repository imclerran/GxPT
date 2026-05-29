using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Exercises the pure JSON-to-model path. A null client is fine here: LoadFromJson adds
    // messages directly to History and never triggers naming or network calls.
    public class ConversationStoreTests
    {
        [Fact]
        public void LoadFromJson_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(ConversationStore.LoadFromJson(null, null));
            Assert.Null(ConversationStore.LoadFromJson(null, ""));
        }

        [Fact]
        public void LoadFromJson_ParsesNameModelAndMessages()
        {
            string json =
                "{\"Id\":\"abc123\",\"Name\":\"My Chat\",\"SelectedModel\":\"openai/gpt-4o\"," +
                "\"Messages\":[" +
                "{\"Role\":\"user\",\"Content\":\"hello\"}," +
                "{\"Role\":\"assistant\",\"Content\":\"hi there\"}]}";

            var convo = ConversationStore.LoadFromJson(null, json);

            Assert.NotNull(convo);
            Assert.Equal("abc123", convo.Id);
            Assert.Equal("My Chat", convo.Name);
            Assert.Equal("openai/gpt-4o", convo.SelectedModel);
            Assert.Equal(2, convo.History.Count);
            Assert.Equal("user", convo.History[0].Role);
            Assert.Equal("hello", convo.History[0].Content);
            Assert.Equal("assistant", convo.History[1].Role);
            Assert.Equal("hi there", convo.History[1].Content);
        }

        [Fact]
        public void LoadFromJson_MissingName_DefaultsToNewConversation()
        {
            string json = "{\"Messages\":[{\"Role\":\"user\",\"Content\":\"x\"}]}";
            var convo = ConversationStore.LoadFromJson(null, json);
            Assert.NotNull(convo);
            Assert.Equal("New Conversation", convo.Name);
        }

        [Fact]
        public void LoadFromJson_ExtractsLegacyAttachmentMarkers()
        {
            // Older saves embedded attachments inline in the message content using markers.
            // LoadFromJson should split them back out into Attachments when no Attachments field exists.
            string content =
                "Here is my question" +
                "\\n--- Attached File: foo.txt ---" +
                "\\nhello world" +
                "\\n--- End Attached File: foo.txt ---";
            string json = "{\"Name\":\"Chat\",\"Messages\":[{\"Role\":\"user\",\"Content\":\"" + content + "\"}]}";

            var convo = ConversationStore.LoadFromJson(null, json);

            Assert.NotNull(convo);
            Assert.Single(convo.History);
            var msg = convo.History[0];
            Assert.Equal("Here is my question", msg.Content);
            Assert.NotNull(msg.Attachments);
            Assert.Single(msg.Attachments);
            Assert.Equal("foo.txt", msg.Attachments[0].FileName);
            Assert.Contains("hello world", msg.Attachments[0].Content);
        }
    }
}
