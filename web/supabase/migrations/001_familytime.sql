-- Apply once to a dedicated Supabase project. No browser/device receives the service key.
create table public.devices (
  id uuid primary key,
  owner_id uuid not null references auth.users(id) on delete cascade,
  name text not null check (length(name) between 1 and 100),
  created_at timestamptz not null default now(),
  last_seen_at timestamptz,
  revoked_at timestamptz,
  metadata jsonb not null default '{}'::jsonb
);
create index devices_owner on public.devices(owner_id);
create table public.device_secrets (
  device_id uuid primary key references public.devices(id) on delete cascade,
  token_hash text not null unique check (token_hash ~ '^[0-9a-f]{64}$')
);
create table public.pairing_codes (
  id uuid primary key default gen_random_uuid(),
  owner_id uuid not null references auth.users(id) on delete cascade,
  code_hash text not null unique check (code_hash ~ '^[0-9a-f]{64}$'),
  expires_at timestamptz not null,
  consumed_at timestamptz,
  claimed_device uuid references public.devices(id) on delete set null
);
create table public.daily_snapshots (
  device_id uuid not null references public.devices(id) on delete cascade,
  day date not null,
  revision bigint not null check (revision > 0),
  payload jsonb not null,
  received_at timestamptz not null default now(),
  primary key (device_id, day)
);
create table public.request_limits (
  key text primary key,
  window_start timestamptz not null,
  hits integer not null
);
alter table public.devices enable row level security;
alter table public.device_secrets enable row level security;
alter table public.pairing_codes enable row level security;
alter table public.daily_snapshots enable row level security;
alter table public.request_limits enable row level security;
revoke all on public.devices, public.device_secrets, public.pairing_codes, public.daily_snapshots, public.request_limits from anon, authenticated;
grant select on public.devices, public.daily_snapshots to authenticated;
grant all on public.devices, public.device_secrets, public.pairing_codes, public.daily_snapshots, public.request_limits to service_role;
create policy own_devices on public.devices for select to authenticated using (owner_id = (select auth.uid()));
create policy own_days on public.daily_snapshots for select to authenticated using (
  exists (select 1 from public.devices d where d.id = device_id and d.owner_id = (select auth.uid()))
);

create function public.consume_request_limit(p_key text, p_limit integer, p_seconds integer)
returns boolean language plpgsql security definer set search_path = '' as $$
declare current_hits integer;
begin
  insert into public.request_limits as r(key, window_start, hits) values(p_key, now(), 1)
  on conflict(key) do update set
    hits = case when r.window_start < now() - make_interval(secs => p_seconds) then 1 else r.hits + 1 end,
    window_start = case when r.window_start < now() - make_interval(secs => p_seconds) then now() else r.window_start end
  returning hits into current_hits;
  return current_hits <= p_limit;
end $$;

create function public.claim_family_device(p_code_hash text, p_device_id uuid, p_token_hash text, p_name text)
returns uuid language plpgsql security definer set search_path = '' as $$
declare pairing public.pairing_codes%rowtype;
begin
  select * into pairing from public.pairing_codes where code_hash=p_code_hash for update;
  if not found then raise exception 'invalid_code' using errcode='22023'; end if;
  if pairing.consumed_at is not null then
    if pairing.claimed_device=p_device_id and exists (
      select 1 from public.device_secrets s join public.devices d on d.id=s.device_id
      where s.device_id=p_device_id and s.token_hash=p_token_hash and d.revoked_at is null
    ) then return p_device_id; end if;
    raise exception 'used_code' using errcode='23505';
  end if;
  if pairing.expires_at <= now() then raise exception 'invalid_code' using errcode='22023'; end if;
  if (select count(*) from public.devices where owner_id=pairing.owner_id) >= 20 then
    raise exception 'device_limit' using errcode='22023';
  end if;
  insert into public.devices(id,owner_id,name) values(p_device_id,pairing.owner_id,p_name);
  insert into public.device_secrets(device_id,token_hash) values(p_device_id,p_token_hash);
  update public.pairing_codes set consumed_at=now(),claimed_device=p_device_id where id=pairing.id;
  return p_device_id;
end $$;

create function public.sync_family_device(p_token_hash text, p_metadata jsonb, p_days jsonb)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare device public.devices%rowtype; item jsonb; accepted jsonb := '[]'::jsonb;
begin
  select d.* into device from public.devices d join public.device_secrets s on s.device_id=d.id
    where s.token_hash=p_token_hash and d.revoked_at is null for update of d;
  if not found then raise exception 'device_revoked' using errcode='42501'; end if;
  if jsonb_array_length(p_days)>7 then raise exception 'batch_too_large' using errcode='22023'; end if;
  update public.devices set last_seen_at=now(), metadata=p_metadata where id=device.id;
  for item in select value from jsonb_array_elements(p_days) loop
    insert into public.daily_snapshots as stored(device_id,day,revision,payload)
      values(device.id,(item->>'day')::date,(item->>'revision')::bigint,item)
    on conflict(device_id,day) do update set revision=excluded.revision,received_at=now(),
      payload=case when excluded.payload->'sessions'='null'::jsonb
        then jsonb_set(excluded.payload,'{sessions}',coalesce(stored.payload->'sessions','null'::jsonb))
        else excluded.payload end
    where stored.revision < excluded.revision;
    accepted := accepted || jsonb_build_array(jsonb_build_object('day',item->>'day','revision',(item->>'revision')::bigint));
  end loop;
  delete from public.daily_snapshots where device_id=device.id
    and day < (now() at time zone (p_metadata->>'timeZone'))::date - least(730,greatest(7,(p_metadata->>'summaryDays')::integer));
  return jsonb_build_object('accepted',accepted);
end $$;

revoke all on function public.consume_request_limit(text,integer,integer) from public, anon, authenticated;
revoke all on function public.claim_family_device(text,uuid,text,text) from public, anon, authenticated;
revoke all on function public.sync_family_device(text,jsonb,jsonb) from public, anon, authenticated;
grant execute on function public.consume_request_limit(text,integer,integer) to service_role;
grant execute on function public.claim_family_device(text,uuid,text,text) to service_role;
grant execute on function public.sync_family_device(text,jsonb,jsonb) to service_role;
