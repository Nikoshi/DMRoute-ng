PROJECT_NAME := DMRoute-ng
PROJECT_CONFIG ?= Release
EDITOR := rider
DOC_OUTPUT_DIR ?= artifacts/docs
DOC_SOURCES := $(shell find docs -type f -name '*.adoc' ! -name 'handbook.adoc' ! -name 'hardware-tests.adoc')
PLAYGROUND_ARGS ?=
OPENDMR_BUILD_DIR ?= artifacts/opendmr

include dotnet.mk

.PHONY: docs docs-check docs-html docs-pdf opendmr-codec opendmr-test playground-codec blip25-encoder blip25-test

opendmr-codec:
	rm -rf $(OPENDMR_BUILD_DIR)
	mkdir -p $(OPENDMR_BUILD_DIR)/source
	cp -R third_party/OpenDMR/. $(OPENDMR_BUILD_DIR)/source/
	$(MAKE) -C $(OPENDMR_BUILD_DIR)/source dmr_codec
	cp $(OPENDMR_BUILD_DIR)/source/dmr_codec $(OPENDMR_BUILD_DIR)/dmr_codec

opendmr-test: opendmr-codec
	$(MAKE) -C $(OPENDMR_BUILD_DIR)/source test

test: opendmr-test

playground-codec: opendmr-codec
	dotnet run --project tools/DMRoute_ng.Playground/DMRoute_ng.Playground.csproj -c $(PROJECT_CONFIG) -- --open-dmr $(OPENDMR_BUILD_DIR)/dmr_codec $(PLAYGROUND_ARGS)

blip25-encoder:
	cargo build --locked --release --manifest-path tools/dmroute-blip25-encoder/Cargo.toml

blip25-test:
	cargo test --locked --release --manifest-path tools/dmroute-blip25-encoder/Cargo.toml

docs: docs-html docs-pdf

docs-check:
	@set -e; for file in $(DOC_SOURCES); do \
		bundle exec asciidoctor --failure-level WARN --out-file /dev/null "$$file"; \
	done

docs-html: docs-check
	mkdir -p $(DOC_OUTPUT_DIR)
	bundle exec asciidoctor --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Handbuch.html docs/handbook.adoc
	bundle exec asciidoctor --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Hardwaretests.html docs/hardware-tests.adoc

docs-pdf: docs-check
	mkdir -p $(DOC_OUTPUT_DIR)
	bundle exec asciidoctor-pdf --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Handbuch.pdf docs/handbook.adoc
	bundle exec asciidoctor-pdf --failure-level WARN -D $(DOC_OUTPUT_DIR) -o DMRoute-ng-Hardwaretests.pdf docs/hardware-tests.adoc
