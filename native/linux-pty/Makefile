CC ?= cc
CFLAGS ?= -O2 -Wall -Wextra -Wpedantic -std=c11
LDFLAGS ?=

.PHONY: all clean
all: xas-linux-pty

xas-linux-pty: linux_pty.c
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ $< -lutil

clean:
	rm -f xas-linux-pty
