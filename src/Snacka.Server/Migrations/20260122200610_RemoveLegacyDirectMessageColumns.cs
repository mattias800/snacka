using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacka.Server.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyDirectMessageColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 0: Clean up any existing duplicate 1:1 conversations
            // Keep the one with messages (or the oldest if none have messages)
            migrationBuilder.Sql(@"
                -- Find and merge duplicate 1:1 conversations
                WITH conversation_pairs AS (
                    -- Get all 1:1 conversations with their participant pairs
                    SELECT
                        c.""Id"" as conv_id,
                        LEAST(cp1.""UserId"", cp2.""UserId"") as user1,
                        GREATEST(cp1.""UserId"", cp2.""UserId"") as user2,
                        c.""CreatedAt"",
                        (SELECT COUNT(*) FROM ""DirectMessages"" dm WHERE dm.""ConversationId"" = c.""Id"") as msg_count
                    FROM ""Conversations"" c
                    JOIN ""ConversationParticipants"" cp1 ON cp1.""ConversationId"" = c.""Id""
                    JOIN ""ConversationParticipants"" cp2 ON cp2.""ConversationId"" = c.""Id"" AND cp2.""UserId"" > cp1.""UserId""
                    WHERE c.""IsGroup"" = false
                ),
                ranked_conversations AS (
                    -- Rank conversations for each user pair (prefer ones with messages, then oldest)
                    SELECT
                        conv_id,
                        user1,
                        user2,
                        ROW_NUMBER() OVER (
                            PARTITION BY user1, user2
                            ORDER BY msg_count DESC, ""CreatedAt"" ASC
                        ) as rn
                    FROM conversation_pairs
                ),
                conversations_to_keep AS (
                    SELECT conv_id FROM ranked_conversations WHERE rn = 1
                ),
                conversations_to_delete AS (
                    SELECT conv_id FROM ranked_conversations WHERE rn > 1
                )
                -- Move messages from duplicate conversations to the one we're keeping
                UPDATE ""DirectMessages"" dm
                SET ""ConversationId"" = (
                    SELECT ctk.conv_id
                    FROM conversations_to_keep ctk
                    JOIN ranked_conversations rc_del ON rc_del.conv_id = dm.""ConversationId"" AND rc_del.rn > 1
                    JOIN ranked_conversations rc_keep ON rc_keep.conv_id = ctk.conv_id
                        AND rc_keep.user1 = rc_del.user1 AND rc_keep.user2 = rc_del.user2
                    LIMIT 1
                )
                WHERE ""ConversationId"" IN (SELECT conv_id FROM conversations_to_delete);
            ");

            migrationBuilder.Sql(@"
                -- Delete the duplicate conversations (now empty)
                WITH conversation_pairs AS (
                    SELECT
                        c.""Id"" as conv_id,
                        LEAST(cp1.""UserId"", cp2.""UserId"") as user1,
                        GREATEST(cp1.""UserId"", cp2.""UserId"") as user2,
                        c.""CreatedAt"",
                        (SELECT COUNT(*) FROM ""DirectMessages"" dm WHERE dm.""ConversationId"" = c.""Id"") as msg_count
                    FROM ""Conversations"" c
                    JOIN ""ConversationParticipants"" cp1 ON cp1.""ConversationId"" = c.""Id""
                    JOIN ""ConversationParticipants"" cp2 ON cp2.""ConversationId"" = c.""Id"" AND cp2.""UserId"" > cp1.""UserId""
                    WHERE c.""IsGroup"" = false
                ),
                ranked_conversations AS (
                    SELECT
                        conv_id,
                        ROW_NUMBER() OVER (
                            PARTITION BY user1, user2
                            ORDER BY msg_count DESC, ""CreatedAt"" ASC
                        ) as rn
                    FROM conversation_pairs
                )
                DELETE FROM ""Conversations""
                WHERE ""Id"" IN (SELECT conv_id FROM ranked_conversations WHERE rn > 1);
            ");

            // Step 1: First, update legacy messages to use EXISTING conversations if one exists
            // This handles the case where users already created a conversation via the new system
            migrationBuilder.Sql(@"
                -- Update legacy DirectMessages to use existing conversations between the same users
                UPDATE ""DirectMessages"" dm
                SET ""ConversationId"" = existing_conv.conv_id
                FROM (
                    SELECT DISTINCT ON (dm2.""Id"")
                        dm2.""Id"" as msg_id,
                        c.""Id"" as conv_id
                    FROM ""DirectMessages"" dm2
                    JOIN ""Conversations"" c ON c.""IsGroup"" = false
                    JOIN ""ConversationParticipants"" cp1 ON cp1.""ConversationId"" = c.""Id"" AND cp1.""UserId"" = dm2.""SenderId""
                    JOIN ""ConversationParticipants"" cp2 ON cp2.""ConversationId"" = c.""Id"" AND cp2.""UserId"" = dm2.""RecipientId""
                    WHERE dm2.""ConversationId"" IS NULL
                      AND dm2.""RecipientId"" IS NOT NULL
                ) existing_conv
                WHERE dm.""Id"" = existing_conv.msg_id;
            ");

            // Step 2: Create conversations ONLY for legacy DM pairs that don't have an existing conversation
            migrationBuilder.Sql(@"
                -- Create conversations for legacy DMs that still don't have one
                WITH legacy_pairs AS (
                    SELECT DISTINCT
                        LEAST(""SenderId"", ""RecipientId"") as user1,
                        GREATEST(""SenderId"", ""RecipientId"") as user2,
                        MIN(""CreatedAt"") as first_message_at
                    FROM ""DirectMessages""
                    WHERE ""ConversationId"" IS NULL
                      AND ""RecipientId"" IS NOT NULL
                    GROUP BY LEAST(""SenderId"", ""RecipientId""), GREATEST(""SenderId"", ""RecipientId"")
                )
                INSERT INTO ""Conversations"" (""Id"", ""Name"", ""IconFileName"", ""IsGroup"", ""CreatedAt"", ""CreatedById"")
                SELECT
                    gen_random_uuid(),
                    NULL,
                    NULL,
                    false,
                    first_message_at,
                    user1
                FROM legacy_pairs lp
                -- Only create if no existing 1:1 conversation exists between these users
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""Conversations"" c
                    JOIN ""ConversationParticipants"" cp1 ON cp1.""ConversationId"" = c.""Id"" AND cp1.""UserId"" = lp.user1
                    JOIN ""ConversationParticipants"" cp2 ON cp2.""ConversationId"" = c.""Id"" AND cp2.""UserId"" = lp.user2
                    WHERE c.""IsGroup"" = false
                );
            ");

            // Step 3: Create participants for the newly created conversations
            migrationBuilder.Sql(@"
                -- Add participants to newly created conversations
                INSERT INTO ""ConversationParticipants"" (""Id"", ""ConversationId"", ""UserId"", ""JoinedAt"", ""AddedById"")
                SELECT
                    gen_random_uuid(),
                    c.""Id"",
                    u.user_id,
                    c.""CreatedAt"",
                    NULL
                FROM ""Conversations"" c
                CROSS JOIN LATERAL (
                    SELECT unnest(ARRAY[c.""CreatedById"", (
                        SELECT GREATEST(dm.""SenderId"", dm.""RecipientId"")
                        FROM ""DirectMessages"" dm
                        WHERE dm.""ConversationId"" IS NULL
                          AND LEAST(dm.""SenderId"", dm.""RecipientId"") = c.""CreatedById""
                        LIMIT 1
                    )]) as user_id
                ) u
                WHERE c.""IsGroup"" = false
                  AND u.user_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ConversationParticipants"" cp
                      WHERE cp.""ConversationId"" = c.""Id"" AND cp.""UserId"" = u.user_id
                  );
            ");

            // Step 4: Update remaining legacy DirectMessages to use newly created conversations
            migrationBuilder.Sql(@"
                -- Update remaining legacy DirectMessages with their conversation IDs
                UPDATE ""DirectMessages"" dm
                SET ""ConversationId"" = c.""Id""
                FROM ""Conversations"" c
                JOIN ""ConversationParticipants"" cp1 ON cp1.""ConversationId"" = c.""Id"" AND cp1.""UserId"" = dm.""SenderId""
                JOIN ""ConversationParticipants"" cp2 ON cp2.""ConversationId"" = c.""Id"" AND cp2.""UserId"" = dm.""RecipientId""
                WHERE dm.""ConversationId"" IS NULL
                  AND dm.""RecipientId"" IS NOT NULL
                  AND c.""IsGroup"" = false;
            ");

            // Step 5: Create read states for participants that don't have one
            migrationBuilder.Sql(@"
                -- Create read states for conversation participants that don't have one
                INSERT INTO ""ConversationReadStates"" (""Id"", ""ConversationId"", ""UserId"", ""LastReadMessageId"", ""LastReadAt"")
                SELECT
                    gen_random_uuid(),
                    cp.""ConversationId"",
                    cp.""UserId"",
                    NULL,
                    NULL
                FROM ""ConversationParticipants"" cp
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""ConversationReadStates"" crs
                    WHERE crs.""ConversationId"" = cp.""ConversationId""
                      AND crs.""UserId"" = cp.""UserId""
                );
            ");

            // Step 6: Delete any orphaned DirectMessages that couldn't be migrated
            migrationBuilder.Sql(@"
                DELETE FROM ""DirectMessages"" WHERE ""ConversationId"" IS NULL;
            ");

            // Now safe to drop the legacy columns and make ConversationId required
            migrationBuilder.DropForeignKey(
                name: "FK_DirectMessages_Users_RecipientId",
                table: "DirectMessages");

            migrationBuilder.DropIndex(
                name: "IX_DirectMessages_RecipientId",
                table: "DirectMessages");

            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "DirectMessages");

            migrationBuilder.DropColumn(
                name: "RecipientId",
                table: "DirectMessages");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "DirectMessages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "DirectMessages",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "DirectMessages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipientId",
                table: "DirectMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_RecipientId",
                table: "DirectMessages",
                column: "RecipientId");

            migrationBuilder.AddForeignKey(
                name: "FK_DirectMessages_Users_RecipientId",
                table: "DirectMessages",
                column: "RecipientId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
