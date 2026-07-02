using Iris.Domain.AiIntegration;
using Iris.Domain.Conversations.Entities;
using Iris.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iris.Infrastructure.Projectors
{
    /// <summary>
    /// Shared idempotency-check + insert + read-model bump logic for the two event
    /// types that append a <see cref="ConversationMessage"/> row (MessageSent,
    /// AssistantResponseCompleted). Both projectors call this with their own
    /// event-specific id/role/content.
    /// </summary>
    internal static class ConversationMessageProjection
    {
        public static async Task AppendMessageAsync(
            AppDbContext db,
            Guid messageId,
            Guid conversationId,
            ChatRole role,
            string content,
            DateTimeOffset occurredAt,
            CancellationToken ct)
        {
            var existing = await db.ConversationMessages.FindAsync([messageId], ct);
            if (existing != null) return; // already projected

            var message = new ConversationMessage
            {
                Id = messageId,
                ConversationId = conversationId,
                Role = role,
                Content = content,
                CreatedAt = occurredAt
            };
            db.ConversationMessages.Add(message);

            var conversation = await db.ConversationReadModels.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
            if (conversation != null)
            {
                conversation.MessageCount++;
                conversation.LastMessageAt = occurredAt;
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
