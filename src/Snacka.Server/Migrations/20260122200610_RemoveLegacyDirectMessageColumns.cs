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
            // Step 1: Create conversations for existing legacy DirectMessages that don't have one
            // Find unique sender/recipient pairs and create 1:1 conversations for them
            migrationBuilder.Sql(@"
                -- Create conversations for legacy DMs (where ConversationId is null)
                WITH legacy_pairs AS (
                    SELECT DISTINCT
                        LEAST(""SenderId"", ""RecipientId"") as user1,
                        GREATEST(""SenderId"", ""RecipientId"") as user2,
                        MIN(""CreatedAt"") as first_message_at
                    FROM ""DirectMessages""
                    WHERE ""ConversationId"" IS NULL
                      AND ""RecipientId"" IS NOT NULL
                    GROUP BY LEAST(""SenderId"", ""RecipientId""), GREATEST(""SenderId"", ""RecipientId"")
                ),
                new_conversations AS (
                    INSERT INTO ""Conversations"" (""Id"", ""Name"", ""IconFileName"", ""IsGroup"", ""CreatedAt"", ""CreatedById"")
                    SELECT
                        gen_random_uuid(),
                        NULL,
                        NULL,
                        false,
                        first_message_at,
                        user1
                    FROM legacy_pairs
                    RETURNING ""Id"", ""CreatedById"" as user1, ""CreatedAt""
                )
                -- Store the mapping for the next steps
                SELECT * FROM new_conversations;
            ");

            // Step 2: Create participants for the new conversations
            migrationBuilder.Sql(@"
                -- Add participants to conversations created for legacy DMs
                INSERT INTO ""ConversationParticipants"" (""Id"", ""ConversationId"", ""UserId"", ""JoinedAt"", ""AddedById"")
                SELECT
                    gen_random_uuid(),
                    c.""Id"",
                    dm_users.user_id,
                    c.""CreatedAt"",
                    NULL
                FROM ""Conversations"" c
                CROSS JOIN LATERAL (
                    SELECT DISTINCT unnest(ARRAY[dm.""SenderId"", dm.""RecipientId""]) as user_id
                    FROM ""DirectMessages"" dm
                    WHERE dm.""ConversationId"" IS NULL
                      AND dm.""RecipientId"" IS NOT NULL
                      AND c.""IsGroup"" = false
                      AND c.""CreatedById"" = LEAST(dm.""SenderId"", dm.""RecipientId"")
                      AND c.""CreatedAt"" = (
                          SELECT MIN(dm2.""CreatedAt"")
                          FROM ""DirectMessages"" dm2
                          WHERE dm2.""ConversationId"" IS NULL
                            AND LEAST(dm2.""SenderId"", dm2.""RecipientId"") = LEAST(dm.""SenderId"", dm.""RecipientId"")
                            AND GREATEST(dm2.""SenderId"", dm2.""RecipientId"") = GREATEST(dm.""SenderId"", dm.""RecipientId"")
                      )
                ) dm_users
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""ConversationParticipants"" cp
                    WHERE cp.""ConversationId"" = c.""Id"" AND cp.""UserId"" = dm_users.user_id
                );
            ");

            // Step 3: Update DirectMessages to set ConversationId
            migrationBuilder.Sql(@"
                -- Update legacy DirectMessages with their conversation IDs
                UPDATE ""DirectMessages"" dm
                SET ""ConversationId"" = c.""Id""
                FROM ""Conversations"" c
                WHERE dm.""ConversationId"" IS NULL
                  AND dm.""RecipientId"" IS NOT NULL
                  AND c.""IsGroup"" = false
                  AND EXISTS (
                      SELECT 1 FROM ""ConversationParticipants"" cp1
                      JOIN ""ConversationParticipants"" cp2 ON cp1.""ConversationId"" = cp2.""ConversationId""
                      WHERE cp1.""ConversationId"" = c.""Id""
                        AND cp1.""UserId"" = dm.""SenderId""
                        AND cp2.""UserId"" = dm.""RecipientId""
                  );
            ");

            // Step 4: Create read states for participants
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

            // Step 5: Delete any orphaned DirectMessages that couldn't be migrated
            // (messages where sender or recipient no longer exists)
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
