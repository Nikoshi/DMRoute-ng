ifdef USE_KAITAI
.PHONY: help run add rm list restore force-gen clean distclean edit docker docker.tar
else
.PHONY: help run add rm list restore clean distclean edit docker docker.tar
endif

MAKEFLAGS += --silent

PROJECT_NAME ?=$(shell basename `pwd`) # TODO: Fix
PROJECT_CONFIG ?= Debug
NET_VERSION ?= net8.0
BINARY = bin/$(PROJECT_CONFIG)/$(NET_VERSION)/$(PROJECT_NAME).dll
CS_FILES = $(shell find . -type f -name '*.cs')

ifdef USE_KAITAI
GEN_CS_FILES = $(shell find $(GEN_FOLDER)/ -type f -name '*.cs')
GEN_FILES = $(shell find . -type f -name '*.kys')
GEN_NAMESPACE := $(PROJECT_NAME).$(GEN_FOLDER)
endif

EDITOR ?= rider

DOCKER_IMAGE_NAME ?= $(shell echo $(PROJECT_NAME) | tr '[:upper:]' '[:lower:]' )
DOCKER_IMAGE_TAG ?= latest

### Targets ###

ifdef USE_KAITAI
$(BINARY): $(CS_FILES) $(GEN_CS_FILES)
	dotnet build -c $(PROJECT_CONFIG)

$(GEN_CS_FILES): $(GEN_FILES)
	mkdir -p Packets
	kaitai-struct-compiler $(GEN_FILES) --outdir $(GEN_FOLDER)/ --target csharp --dotnet-namespace $(GEN_NAMESPACE)
else
$(BINARY): $(CS_FILES)
	dotnet build -c $(PROJECT_CONFIG)
endif

help:
	@echo "Target         | Description"
	@echo "---------------+------------------------------------------"
	@echo "help           | Shows this help"
	@echo "run            | Runs the project"
	@echo "add PKG=<name> | Adds package"
	@echo "rm PKG=<name>  | Removes package"
	@echo "list           | List packages"
	@echo "restore        | Restores project"
ifdef USE_KAITAI
	@echo "force-gen      | Force generate Kaitai Structs"
endif
	@echo "clean          | Removes build artefacts"
	@echo "distclean      | Removes all build artefacts"
	@echo "edit           | Open project in rider"
	@echo "docker         | Build docker image"
	@echo "docker.tar     | Build docker image and export as tar file"
	@echo ""
	@echo "Default Action is to compile the project"

run: $(BINARY)
	dotnet run -c $(PROJECT_CONFIG)

add:
	test $(PKG)
	dotnet add package $(PKG)

rm:
	test $(PKG)
	dotnet remove package $(PKG)

list:
	dotnet list package

restore:
	dotnet restore

ifdef USE_KAITAI
force-gen: $(GEN_FILES)
	mkdir -p Packets
	kaitai-struct-compiler $(GEN_FILES) --outdir $(GEN_FOLDER)/ --target csharp --dotnet-namespace $(GEN_NAMESPACE)
endif

clean:
	dotnet clean
	rm -f ${DOCKER_IMAGE_NAME}-${DOCKER_IMAGE_TAG}.tar

distclean: clean
	rm -rf bin/
	rm -rf obj/

edit:
	$(EDITOR) `pwd`

docker: Dockerfile
	docker buildx build -t ${DOCKER_IMAGE_NAME}:${DOCKER_IMAGE_TAG} -f Dockerfile .

docker.tar: docker
	docker image save -o ${DOCKER_IMAGE_NAME}-${DOCKER_IMAGE_TAG}.tar ${DOCKER_IMAGE_NAME}:${DOCKER_IMAGE_TAG}
