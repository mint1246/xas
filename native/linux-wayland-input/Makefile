CC ?= cc
CFLAGS ?= -O2 -Wall -Wextra -Wpedantic -std=c11
PKG_CONFIG ?= pkg-config

.PHONY: all clean
all: xas-wayland-eis

xas-wayland-eis: wayland_eis.c
	$(CC) $(CFLAGS) $< -o $@ $$($(PKG_CONFIG) --cflags --libs libei-1.0 liboeffis-1.0 gio-2.0)

clean:
	rm -f xas-wayland-eis
