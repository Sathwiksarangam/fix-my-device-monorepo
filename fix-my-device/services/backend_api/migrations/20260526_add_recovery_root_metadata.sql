alter table if exists public.devices
  add column if not exists created_at timestamp with time zone default now();

alter table if exists public.devices
  add column if not exists updated_at timestamp with time zone default now();

create table if not exists public.recovery_settings (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public.app_users(id),
  device_id uuid not null references public.devices(id),
  device_name text not null,
  enabled boolean not null default false,
  approved_locations jsonb not null default '[]'::jsonb,
  last_synced_at timestamp with time zone,
  created_at timestamp with time zone default now(),
  updated_at timestamp with time zone default now()
);

alter table if exists public.recovery_settings
  add column if not exists settings_updated_at timestamp with time zone;

alter table if exists public.recovery_settings
  add column if not exists scan_requested_at timestamp with time zone;

create unique index if not exists idx_recovery_settings_owner_device
  on public.recovery_settings(user_id, device_id);

create index if not exists idx_recovery_settings_user
  on public.recovery_settings(user_id);

create table if not exists public.recovery_file_listings (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public.app_users(id),
  device_id uuid not null references public.devices(id),
  file_name text not null,
  full_path text not null,
  extension text,
  size_bytes bigint not null default 0,
  last_modified_at timestamp with time zone,
  is_directory boolean not null default false,
  drive_letter text,
  created_at timestamp with time zone default now(),
  updated_at timestamp with time zone default now()
);

alter table if exists public.recovery_file_listings
  add column if not exists root_label text not null default '';

alter table if exists public.recovery_file_listings
  add column if not exists root_path text not null default '';

create unique index if not exists idx_recovery_file_owner_device_path
  on public.recovery_file_listings(user_id, device_id, full_path);

create index if not exists idx_recovery_file_owner_device
  on public.recovery_file_listings(user_id, device_id);

create index if not exists idx_recovery_file_last_modified
  on public.recovery_file_listings(last_modified_at);
