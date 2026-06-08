namespace GxPT
{
    // Distinguishes the two families of slash command. Prompt commands expand into a message that is
    // sent to the model; client commands run a local handler and suppress the send. v1 ships prompt
    // commands only, but the dispatch pipeline is kind-agnostic so client commands drop in later
    // (architecture: slash-command subsystem).
    internal enum SlashCommandKind
    {
        Prompt = 0,
        Client = 1
    }
}
