using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Snacka.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DirectMessages_Users_RecipientId",
                table: "DirectMessages");

            migrationBuilder.AlterColumn<Guid>(
                name: "RecipientId",
                table: "DirectMessages",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "DirectMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "DirectMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    IconFileName = table.Column<string>(type: "text", nullable: true),
                    IsGroup = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ConversationParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationParticipants_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationParticipants_Users_AddedById",
                        column: x => x.AddedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConversationParticipants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationReadStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastReadMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationReadStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationReadStates_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationReadStates_DirectMessages_LastReadMessageId",
                        column: x => x.LastReadMessageId,
                        principalTable: "DirectMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConversationReadStates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_ConversationId",
                table: "DirectMessages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationParticipants_AddedById",
                table: "ConversationParticipants",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationParticipants_ConversationId_UserId",
                table: "ConversationParticipants",
                columns: new[] { "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationParticipants_UserId",
                table: "ConversationParticipants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReadStates_ConversationId_UserId",
                table: "ConversationReadStates",
                columns: new[] { "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReadStates_LastReadMessageId",
                table: "ConversationReadStates",
                column: "LastReadMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReadStates_UserId",
                table: "ConversationReadStates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CreatedAt",
                table: "Conversations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CreatedById",
                table: "Conversations",
                column: "CreatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_DirectMessages_Conversations_ConversationId",
                table: "DirectMessages",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DirectMessages_Users_RecipientId",
                table: "DirectMessages",
                column: "RecipientId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DirectMessages_Conversations_ConversationId",
                table: "DirectMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_DirectMessages_Users_RecipientId",
                table: "DirectMessages");

            migrationBuilder.DropTable(
                name: "ConversationParticipants");

            migrationBuilder.DropTable(
                name: "ConversationReadStates");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_DirectMessages_ConversationId",
                table: "DirectMessages");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "DirectMessages");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "DirectMessages");

            migrationBuilder.AlterColumn<Guid>(
                name: "RecipientId",
                table: "DirectMessages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DirectMessages_Users_RecipientId",
                table: "DirectMessages",
                column: "RecipientId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
