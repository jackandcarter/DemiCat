import discord
from discord.ext import commands


class DemiBot(commands.Bot):
    """Minimal discord.py bot wrapper."""

    def __init__(self, token: str, db):
        intents = discord.Intents.default()
        intents.message_content = True
        super().__init__(command_prefix="!", intents=intents)
        self.token = token
        self.db = db

    async def on_ready(self):
        print(f"Logged in as {self.user} (ID: {self.user.id})")

    async def start_bot(self) -> None:
        await self.start(self.token)
