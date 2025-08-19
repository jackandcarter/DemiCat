from __future__ import annotations

import asyncio
import json
import logging
from datetime import datetime, timedelta
from types import SimpleNamespace

from sqlalchemy import select

from .db.session import get_session
from .db.models import RecurringEvent
from .http.routes.events import create_event, CreateEventBody
from .http.discord_client import discord_client


async def process_recurring_events_once() -> None:
    async for db in get_session():
        now = datetime.utcnow()
        res = await db.execute(select(RecurringEvent))
        events = list(res.scalars())
        for ev in events:
            remove = False
            if discord_client:
                channel = discord_client.get_channel(ev.channel_id)
                if channel is None:
                    remove = True
                else:
                    try:
                        await channel.fetch_message(ev.id)
                    except Exception:
                        remove = True
            if remove:
                await db.delete(ev)
                continue
            if ev.next_post_at <= now:
                payload = json.loads(ev.payload_json)
                payload["time"] = ev.next_post_at.strftime("%Y-%m-%dT%H:%M:%S.%fZ")
                payload["repeat"] = None
                body = CreateEventBody(**payload)
                ctx = SimpleNamespace(guild=SimpleNamespace(id=ev.guild_id))
                try:
                    await create_event(body=body, ctx=ctx, db=db)
                except Exception:
                    logging.exception("Failed to repost event %s", ev.id)
                    continue
                if ev.repeat == "daily":
                    ev.next_post_at += timedelta(days=1)
                elif ev.repeat == "weekly":
                    ev.next_post_at += timedelta(days=7)
        await db.commit()
        break


async def recurring_event_poster() -> None:
    while True:
        try:
            await process_recurring_events_once()
        except Exception:
            logging.exception("Recurring event poster failed")
        await asyncio.sleep(60)
